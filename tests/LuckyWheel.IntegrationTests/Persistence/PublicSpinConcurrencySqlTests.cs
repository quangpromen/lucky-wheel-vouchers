using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Application.Features.Admin;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using LuckyWheel.Application.Features.Public;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using LuckyWheel.Infrastructure.Admin;
using LuckyWheel.Infrastructure.Persistence;
using LuckyWheel.Infrastructure.PrizeKeys;
using LuckyWheel.Infrastructure.Public;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LuckyWheel.IntegrationTests.Persistence;

[Collection("DatabaseCollection")]
public sealed class PublicSpinConcurrencySqlTests(TestDatabaseFixture fixture)
{
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(20);
    private readonly IPrizeKeyProtector _protector = CreateProtector();
    private readonly DateTime _now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ConcurrentSameKey_SameRequestWin_SimultaneousSave_ReturnsIdenticalResultWithSingleSpinKeyAndLock()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 2, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var barrier = new TwoPartySaveBarrier();

        await using var sp1 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty1Interceptor()]);
        await using var sp2 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty2Interceptor()]);

        var idempKey = $"sql-win-same-{Guid.NewGuid():N}";

        var task1 = RunSpinInScopeAsync(sp1, seed.Slug, idempKey, new SpinRequest("Quang.Dev+first@gmail.com", true));
        var task2 = RunSpinInScopeAsync(sp2, seed.Slug, idempKey, new SpinRequest("  quangdev+second@GMAIL.COM ", true));

        await barrier.BothReachedSaveAsync.WaitAsync(BarrierTimeout);
        barrier.ReleaseFirst();
        var res1 = await task1.WaitAsync(BarrierTimeout);

        barrier.ReleaseSecond();
        var res2 = await task2.WaitAsync(BarrierTimeout);

        Assert.Equal("Win", res1.Result);
        Assert.Equal(res1.SpinId, res2.SpinId);
        Assert.Equal(res1.ReceiptToken, res2.ReceiptToken);
        Assert.Equal(res1.Result, res2.Result);
        Assert.Equal(res1.EntryId, res2.EntryId);
        Assert.Equal(res1.DisplayOrder, res2.DisplayOrder);
        Assert.Equal(res1.CompletedAtUtc, res2.CompletedAtUtc);
        Assert.NotNull(res1.Prize);
        Assert.NotNull(res2.Prize);
        Assert.Equal(res1.Prize!.Id, res2.Prize!.Id);
        Assert.Equal(res1.Prize.Key, res2.Prize.Key);
        Assert.Equal(res1.Prize.KeyExpiresAtUtc, res2.Prize.KeyExpiresAtUtc);

        await using var verify = fixture.CreateContext();
        var spins = await verify.SpinHistories.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id).ToListAsync();
        Assert.Single(spins);
        Assert.Equal(SpinResult.Win, spins[0].Result);

        var locks = await verify.WinnerLocks.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id && x.IsActive).ToListAsync();
        Assert.Single(locks);
        Assert.Equal("quangdev@gmail.com", locks[0].EmailNormalized);

        var keys = await verify.PrizeKeys.AsNoTracking().Where(x => x.PrizeId == seed.PrizeA.Id).ToListAsync();
        Assert.Single(keys, k => k.Status == PrizeKeyStatus.Assigned && k.AssignedSpinId == spins[0].Id);
        Assert.Single(keys, k => k.Status == PrizeKeyStatus.Available && k.AssignedSpinId == null);
    }

    [Fact]
    public async Task ConcurrentSameKey_WinnerLockCommittedBeforeSecondRequestChecksWinnerLock_StillReplaysOriginalWin()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 2, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var idempKey = $"sql-lock-before-idemp-{Guid.NewGuid():N}";

        var r2PausedAfterVersion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r2ResumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var r2Interceptor = new CommandPauseInterceptor(
            shouldPauseAfterExecute: cmd => cmd.CommandText.Contains("FROM [WheelVersions] WITH (REPEATABLEREAD, ROWLOCK)", StringComparison.Ordinal),
            onPaused: r2PausedAfterVersion,
            waitGate: r2ResumeGate);

        await using var sp1 = BuildTestServiceProvider(drawValue: 0);
        await using var sp2 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [r2Interceptor]);

        var task2 = RunSpinInScopeAsync(sp2, seed.Slug, idempKey, new SpinRequest("quangdev+retry@GMAIL.COM", true));
        await r2PausedAfterVersion.Task.WaitAsync(BarrierTimeout);

        // Request 1 commits Win + WinnerLock while Request 2 has already passed its initial idempotency lookup
        // and has not checked WinnerLocks yet.
        var res1 = await RunSpinInScopeAsync(sp1, seed.Slug, idempKey, new SpinRequest("Quang.Dev+orig@gmail.com", true)).WaitAsync(BarrierTimeout);
        Assert.Equal("Win", res1.Result);

        r2ResumeGate.SetResult();
        var res2 = await task2.WaitAsync(BarrierTimeout);

        Assert.Equal(res1.SpinId, res2.SpinId);
        Assert.Equal(res1.ReceiptToken, res2.ReceiptToken);
        Assert.Equal(res1.Prize?.Key, res2.Prize?.Key);

        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.SpinHistories.CountAsync(x => x.WheelId == seed.Wheel.Id));
        Assert.Equal(1, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id && x.IsActive));
        Assert.Equal(1, await verify.PrizeKeys.CountAsync(x => x.PrizeId == seed.PrizeA.Id && x.Status == PrizeKeyStatus.Assigned));
    }

    [Fact]
    public async Task ConcurrentSameKey_DirectNoPrize_ReturnsIdenticalResultWithSingleSpinAndNoLock()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var barrier = new TwoPartySaveBarrier();

        await using var sp1 = BuildTestServiceProvider(drawValue: 950_000, interceptorsFactory: () => [barrier.CreateParty1Interceptor()]);
        await using var sp2 = BuildTestServiceProvider(drawValue: 950_000, interceptorsFactory: () => [barrier.CreateParty2Interceptor()]);

        var idempKey = $"sql-np-same-{Guid.NewGuid():N}";

        var task1 = RunSpinInScopeAsync(sp1, seed.Slug, idempKey, new SpinRequest("player.one+a@gmail.com", true));
        var task2 = RunSpinInScopeAsync(sp2, seed.Slug, idempKey, new SpinRequest("playerone+b@gmail.com", true));

        await barrier.BothReachedSaveAsync.WaitAsync(BarrierTimeout);
        barrier.ReleaseFirst();
        var res1 = await task1.WaitAsync(BarrierTimeout);

        barrier.ReleaseSecond();
        var res2 = await task2.WaitAsync(BarrierTimeout);

        Assert.Equal("NoPrize", res1.Result);
        Assert.Null(res1.Prize);
        Assert.Equal(res1.SpinId, res2.SpinId);
        Assert.Equal(res1.ReceiptToken, res2.ReceiptToken);
        Assert.Equal(res1.EntryId, res2.EntryId);
        Assert.Equal(seed.NoPrizeSegment.Id, res1.EntryId);

        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.SpinHistories.CountAsync(x => x.WheelId == seed.Wheel.Id));
        Assert.Equal(0, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id));
        Assert.Equal(1, await verify.PrizeKeys.CountAsync(x => x.PrizeId == seed.PrizeA.Id && x.Status == PrizeKeyStatus.Available));
    }

    [Fact]
    public async Task ConcurrentSameKey_OutOfKeyFallbackNoPrize_ReturnsIdenticalResultWithSingleSpinAndNoLock()
    {
        // Prize A has 0 keys (weight 700_000), Prize B has 1 key (weight 200_000), NoPrize has 100_000.
        var seed = await SeedWheelWithTwoPrizesAsync(prizeAKeys: 0, prizeBKeys: 1);
        var barrier = new TwoPartySaveBarrier();

        await using var sp1 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty1Interceptor()]);
        await using var sp2 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty2Interceptor()]);

        var idempKey = $"sql-fallback-same-{Guid.NewGuid():N}";

        var task1 = RunSpinInScopeAsync(sp1, seed.Slug, idempKey, new SpinRequest("fallback.user@gmail.com", true));
        var task2 = RunSpinInScopeAsync(sp2, seed.Slug, idempKey, new SpinRequest("fallbackuser+retry@gmail.com", true));

        await barrier.BothReachedSaveAsync.WaitAsync(BarrierTimeout);
        barrier.ReleaseFirst();
        var res1 = await task1.WaitAsync(BarrierTimeout);

        barrier.ReleaseSecond();
        var res2 = await task2.WaitAsync(BarrierTimeout);

        Assert.Equal("NoPrize", res1.Result);
        Assert.Null(res1.Prize);
        Assert.Equal(seed.NoPrizeSegment.Id, res1.EntryId);
        Assert.Equal(seed.NoPrizeSegment.DisplayOrder, res1.DisplayOrder);
        Assert.Equal(res1.SpinId, res2.SpinId);
        Assert.Equal(res1.ReceiptToken, res2.ReceiptToken);

        await using var verify = fixture.CreateContext();
        var spins = await verify.SpinHistories.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id).ToListAsync();
        Assert.Single(spins);
        Assert.Equal(SpinResult.NoPrize, spins[0].Result);
        Assert.Equal(0, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id));

        var prizeBKey = await verify.PrizeKeys.AsNoTracking().SingleAsync(x => x.PrizeId == seed.PrizeB!.Id);
        Assert.Equal(PrizeKeyStatus.Available, prizeBKey.Status);
        Assert.Null(prizeBKey.AssignedSpinId);
    }

    [Fact]
    public async Task ConcurrentSameKey_DifferentEmails_OneSavedAndConflictingRequestGetsIdempotencyKeyReusedWithoutLeakingData()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 2, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var barrier = new TwoPartySaveBarrier();

        await using var sp1 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty1Interceptor()]);
        await using var sp2 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty2Interceptor()]);

        var idempKey = $"sql-diff-email-{Guid.NewGuid():N}";

        var task1 = RunSpinInScopeAsync(sp1, seed.Slug, idempKey, new SpinRequest("alice.first@gmail.com", true));
        var task2 = RunSpinInScopeAsync(sp2, seed.Slug, idempKey, new SpinRequest("bob.second@gmail.com", true));

        await barrier.BothReachedSaveAsync.WaitAsync(BarrierTimeout);
        barrier.ReleaseFirst();
        var win = await task1.WaitAsync(BarrierTimeout);

        barrier.ReleaseSecond();
        var conflict = await Assert.ThrowsAsync<ConflictException>(() => task2.WaitAsync(BarrierTimeout));

        Assert.Equal("IDEMPOTENCY_KEY_REUSED", conflict.Code);
        Assert.DoesNotContain(win.Prize!.Key!, conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(win.ReceiptToken, conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", conflict.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.CreateContext();
        var spins = await verify.SpinHistories.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id).ToListAsync();
        Assert.Single(spins);
        Assert.Equal("alicefirst@gmail.com", spins[0].EmailNormalized);

        var locks = await verify.WinnerLocks.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id && x.IsActive).ToListAsync();
        Assert.Single(locks);
        Assert.Equal("alicefirst@gmail.com", locks[0].EmailNormalized);

        var keys = await verify.PrizeKeys.AsNoTracking().Where(x => x.PrizeId == seed.PrizeA.Id).ToListAsync();
        Assert.Single(keys, k => k.Status == PrizeKeyStatus.Assigned);
        Assert.Single(keys, k => k.Status == PrizeKeyStatus.Available);
    }

    [Fact]
    public async Task ConcurrentSameKey_DifferentWheels_OneSavedAndConflictingRequestGetsIdempotencyKeyReused()
    {
        var seedA = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var seedB = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var barrier = new TwoPartySaveBarrier();

        await using var sp1 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty1Interceptor()]);
        await using var sp2 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty2Interceptor()]);

        var idempKey = $"sql-diff-wheel-{Guid.NewGuid():N}";
        const string email = "shared.player@gmail.com";

        var taskA = RunSpinInScopeAsync(sp1, seedA.Slug, idempKey, new SpinRequest(email, true));
        var taskB = RunSpinInScopeAsync(sp2, seedB.Slug, idempKey, new SpinRequest(email, true));

        await barrier.BothReachedSaveAsync.WaitAsync(BarrierTimeout);
        barrier.ReleaseFirst();
        var winA = await taskA.WaitAsync(BarrierTimeout);

        barrier.ReleaseSecond();
        var conflictB = await Assert.ThrowsAsync<ConflictException>(() => taskB.WaitAsync(BarrierTimeout));

        Assert.Equal("IDEMPOTENCY_KEY_REUSED", conflictB.Code);
        Assert.DoesNotContain(winA.Prize!.Key!, conflictB.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(winA.ReceiptToken, conflictB.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.SpinHistories.CountAsync(x => x.WheelId == seedA.Wheel.Id));
        Assert.Equal(0, await verify.SpinHistories.CountAsync(x => x.WheelId == seedB.Wheel.Id));
        Assert.Equal(0, await verify.WinnerLocks.CountAsync(x => x.WheelId == seedB.Wheel.Id));

        var keyB = await verify.PrizeKeys.AsNoTracking().SingleAsync(x => x.PrizeId == seedB.PrizeA.Id);
        Assert.Equal(PrizeKeyStatus.Available, keyB.Status);
        Assert.Null(keyB.AssignedSpinId);
    }

    [Fact]
    public async Task ConcurrentDifferentKeys_SameGmailAliases_AtMostOneWinAndOneActiveLockWithoutLeakedKey()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 2, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var barrier = new TwoPartySaveBarrier();

        await using var sp1 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty1Interceptor()]);
        await using var sp2 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty2Interceptor()]);

        var key1 = $"sql-alias-1-{Guid.NewGuid():N}";
        var key2 = $"sql-alias-2-{Guid.NewGuid():N}";

        var task1 = RunSpinInScopeAsync(sp1, seed.Slug, key1, new SpinRequest("Quang.Dev+one@gmail.com", true));
        var task2 = RunSpinInScopeAsync(sp2, seed.Slug, key2, new SpinRequest("quangdev+two@GMAIL.COM", true));

        await barrier.BothReachedSaveAsync.WaitAsync(BarrierTimeout);
        barrier.ReleaseFirst();
        var win1 = await task1.WaitAsync(BarrierTimeout);

        barrier.ReleaseSecond();
        var conflict2 = await Assert.ThrowsAsync<ConflictException>(() => task2.WaitAsync(BarrierTimeout));

        Assert.Equal("EMAIL_ALREADY_WON", conflict2.Code);
        Assert.Equal("Win", win1.Result);

        await using var verify = fixture.CreateContext();
        var spins = await verify.SpinHistories.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id).ToListAsync();
        Assert.Single(spins);
        Assert.Equal(win1.SpinId, spins[0].Id);
        Assert.Equal(key1, spins[0].IdempotencyKey);

        var locks = await verify.WinnerLocks.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id && x.IsActive).ToListAsync();
        Assert.Single(locks);
        Assert.Equal("quangdev@gmail.com", locks[0].EmailNormalized);

        var keys = await verify.PrizeKeys.AsNoTracking().Where(x => x.PrizeId == seed.PrizeA.Id).ToListAsync();
        Assert.Single(keys, k => k.Status == PrizeKeyStatus.Assigned && k.AssignedSpinId == win1.SpinId);
        Assert.Single(keys, k => k.Status == PrizeKeyStatus.Available && k.AssignedSpinId == null);
    }

    [Fact]
    public async Task ConcurrentDifferentKeys_SameGmailAliases_MultipleNoPrizeSpinsSucceed()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var barrier = new TwoPartySaveBarrier();

        await using var sp1 = BuildTestServiceProvider(drawValue: 950_000, interceptorsFactory: () => [barrier.CreateParty1Interceptor()]);
        await using var sp2 = BuildTestServiceProvider(drawValue: 950_000, interceptorsFactory: () => [barrier.CreateParty2Interceptor()]);

        var task1 = RunSpinInScopeAsync(sp1, seed.Slug, $"sql-np-alias1-{Guid.NewGuid():N}", new SpinRequest("Quang.Dev+np1@gmail.com", true));
        var task2 = RunSpinInScopeAsync(sp2, seed.Slug, $"sql-np-alias2-{Guid.NewGuid():N}", new SpinRequest("quangdev+np2@GMAIL.COM", true));

        await barrier.BothReachedSaveAsync.WaitAsync(BarrierTimeout);
        barrier.ReleaseFirst();
        barrier.ReleaseSecond();

        var results = await Task.WhenAll(task1, task2).WaitAsync(BarrierTimeout);
        Assert.All(results, r => Assert.Equal("NoPrize", r.Result));
        Assert.NotEqual(results[0].SpinId, results[1].SpinId);

        await using var verify = fixture.CreateContext();
        Assert.Equal(2, await verify.SpinHistories.CountAsync(x => x.WheelId == seed.Wheel.Id));
        Assert.Equal(0, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id));
    }

    [Fact]
    public async Task ConcurrentDifferentGmails_LastAvailableKey_OneWinsAndOtherFallsBackToNoPrizeWithoutRedistribution()
    {
        // Prize A has 1 last key (weight 700_000), Prize B has 1 key (weight 200_000), NoPrize has 100_000.
        var seed = await SeedWheelWithTwoPrizesAsync(prizeAKeys: 1, prizeBKeys: 1);

        var r1LockedLastKey = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r1ResumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingProtector = new BlockingPrizeKeyProtector(_protector, r1LockedLastKey, r1ResumeGate);

        await using var sp1 = BuildTestServiceProvider(drawValue: 0, customProtector: blockingProtector);
        await using var sp2 = BuildTestServiceProvider(drawValue: 0);

        // User A allocates the single key of Prize A (holding SQL Server UPDLOCK, ROWLOCK) and pauses before commit.
        var task1 = RunSpinInScopeAsync(sp1, seed.Slug, $"sql-lastkey-a-{Guid.NewGuid():N}", new SpinRequest("usera@gmail.com", true));
        await r1LockedLastKey.Task.WaitAsync(BarrierTimeout);

        // User B draws Prize A while User A holds the lock -> READPAST skips the locked row -> User B gets null -> falls back to NoPrize!
        var res2 = await RunSpinInScopeAsync(sp2, seed.Slug, $"sql-lastkey-b-{Guid.NewGuid():N}", new SpinRequest("userb@gmail.com", true)).WaitAsync(BarrierTimeout);
        Assert.Equal("NoPrize", res2.Result);
        Assert.Null(res2.Prize);
        Assert.Equal(seed.NoPrizeSegment.Id, res2.EntryId);

        // Release User A to complete Win with the last key.
        r1ResumeGate.SetResult();
        var res1 = await task1.WaitAsync(BarrierTimeout);
        Assert.Equal("Win", res1.Result);
        Assert.NotNull(res1.Prize?.Key);

        await using var verify = fixture.CreateContext();
        var spins = await verify.SpinHistories.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id).ToListAsync();
        Assert.Equal(2, spins.Count);
        Assert.Single(spins, s => s.EmailNormalized == "usera@gmail.com" && s.Result == SpinResult.Win && s.PrizeId == seed.PrizeA.Id);
        Assert.Single(spins, s => s.EmailNormalized == "userb@gmail.com" && s.Result == SpinResult.NoPrize && s.PrizeKeyId == null);

        var keyA = await verify.PrizeKeys.AsNoTracking().SingleAsync(x => x.PrizeId == seed.PrizeA.Id);
        Assert.Equal(PrizeKeyStatus.Assigned, keyA.Status);
        Assert.Equal(res1.SpinId, keyA.AssignedSpinId);

        // Prize B's key must NOT be redistributed
        var keyB = await verify.PrizeKeys.AsNoTracking().SingleAsync(x => x.PrizeId == seed.PrizeB!.Id);
        Assert.Equal(PrizeKeyStatus.Available, keyB.Status);
        Assert.Null(keyB.AssignedSpinId);

        var locks = await verify.WinnerLocks.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id && x.IsActive).ToListAsync();
        Assert.Single(locks);
        Assert.Equal("usera@gmail.com", locks[0].EmailNormalized);
    }

    [Fact]
    public async Task Replay_AfterWheelDisabledVersionClosedAndWinnerLockActive_ReturnsOriginalWinAndNoPrizeResults()
    {
        // Start with 0 keys for Prize A so first spin falls back to NoPrize, then replenish 1 key for Win.
        var seed = await SeedWheelAsync(availableKeyCount: 0, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var noPrizeIdempKey = $"sql-replay-np-{Guid.NewGuid():N}";
        var winIdempKey = $"sql-replay-win-{Guid.NewGuid():N}";

        var adminId = await SeedAdminAsync();
        await using var sp = BuildTestServiceProvider(drawValue: 0, adminId: adminId);

        var originalNoPrize = await RunSpinInScopeAsync(sp, seed.Slug, noPrizeIdempKey, new SpinRequest("Quang.Dev+np@gmail.com", true)).WaitAsync(BarrierTimeout);
        Assert.Equal("NoPrize", originalNoPrize.Result);

        await using (var replenishDb = fixture.CreateContext())
        {
            var pk = _protector.Protect($"LW-REPLAY-{Guid.NewGuid():N}"[..20]);
            replenishDb.PrizeKeys.Add(new PrizeKey(seed.PrizeA.Id, pk.CodeHash, pk.EncryptedCode, pk.EncryptionNonce, pk.EncryptionTag, _now));
            await replenishDb.SaveChangesAsync();
        }

        var originalWin = await RunSpinInScopeAsync(sp, seed.Slug, winIdempKey, new SpinRequest("quangdev+win@gmail.com", true)).WaitAsync(BarrierTimeout);
        Assert.Equal("Win", originalWin.Result);
        Assert.NotNull(originalWin.Prize?.Key);

        // Close active version AND disable the wheel
        string vRowVersion;
        await using (var rvDb = fixture.CreateContext())
        {
            var vEntity = await rvDb.WheelVersions.SingleAsync(x => x.Id == seed.Version.Id);
            vRowVersion = Convert.ToBase64String(rvDb.Entry(vEntity).Property<byte[]>("RowVersion").CurrentValue!);
        }

        await using (var adminScope = sp.CreateAsyncScope())
        {
            var adminService = adminScope.ServiceProvider.GetRequiredService<IAdminManagementService>();
            await adminService.CloseVersionAsync(seed.Version.Id, new CloseActiveWheelVersionRequest(vRowVersion), default);

            var adminDb = adminScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var wheelToDisable = await adminDb.Wheels.SingleAsync(x => x.Id == seed.Wheel.Id);
            wheelToDisable.Disable(_now.AddMinutes(5));
            await adminDb.SaveChangesAsync();
        }

        // Replay both NoPrize and Win even though WinnerLock is active, Version is Closed, and Wheel is Disabled
        var replayNoPrize = await RunSpinInScopeAsync(sp, seed.Slug, noPrizeIdempKey, new SpinRequest("  q.u.a.n.g.d.e.v+retry1@GMAIL.COM ", true)).WaitAsync(BarrierTimeout);
        var replayWin = await RunSpinInScopeAsync(sp, seed.Slug, winIdempKey, new SpinRequest("Quang.Dev+retry2@gmail.com", true)).WaitAsync(BarrierTimeout);

        Assert.Equal(originalNoPrize.SpinId, replayNoPrize.SpinId);
        Assert.Equal(originalNoPrize.ReceiptToken, replayNoPrize.ReceiptToken);
        Assert.Equal("NoPrize", replayNoPrize.Result);
        Assert.Equal(seed.NoPrizeSegment.Id, replayNoPrize.EntryId);

        Assert.Equal(originalWin.SpinId, replayWin.SpinId);
        Assert.Equal(originalWin.ReceiptToken, replayWin.ReceiptToken);
        Assert.Equal("Win", replayWin.Result);
        Assert.Equal(originalWin.Prize!.Key, replayWin.Prize!.Key);

        await using var verify = fixture.CreateContext();
        Assert.Equal(2, await verify.SpinHistories.CountAsync(x => x.WheelId == seed.Wheel.Id));
        Assert.Equal(1, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id && x.IsActive));
        Assert.Equal(1, await verify.PrizeKeys.CountAsync(x => x.PrizeId == seed.PrizeA.Id && x.Status == PrizeKeyStatus.Assigned));
    }

    [Fact]
    public async Task Race_SpinAdmittedBeforeCloseVersion_SpinCommitsOnOldVersionBeforeCloseCompletes()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);

        var spinEnteredSelector = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spinResumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingSelector = new BlockingWeightedPrizeSelector(new WeightedPrizeSelector(new TestRandomProvider(0)), spinEnteredSelector, spinResumeGate);

        var closeBlockedOnWheelLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeLockAcquired = 0;
        var closeCommandObserver = new CommandExecutingObserver(
            cmd => cmd.CommandText.Contains("FROM [Wheels] WITH (XLOCK, ROWLOCK)", StringComparison.Ordinal),
            onExecuting: () => closeBlockedOnWheelLock.TrySetResult(),
            onExecuted: () => Interlocked.Exchange(ref closeLockAcquired, 1));

        var adminId = await SeedAdminAsync();
        await using var spinSp = BuildTestServiceProvider(drawValue: 0, customSelector: blockingSelector);
        await using var closeSp = BuildTestServiceProvider(drawValue: 0, adminId: adminId, interceptorsFactory: () => [closeCommandObserver]);

        // Spin acquires S locks on Wheel & Version v1 and pauses before drawing/saving
        var spinTask = RunSpinInScopeAsync(spinSp, seed.Slug, $"sql-close-race1-{Guid.NewGuid():N}", new SpinRequest("earlyplayer@gmail.com", true));
        await spinEnteredSelector.Task.WaitAsync(BarrierTimeout);

        // Admin starts CloseVersionAsync(v1): it must block on XLOCK(Wheels) while Spin holds S lock
        var closeTask = RunCloseVersionInScopeAsync(closeSp, seed.Version.Id);
        await closeBlockedOnWheelLock.Task.WaitAsync(BarrierTimeout);
        Assert.Equal(0, Volatile.Read(ref closeLockAcquired));
        Assert.False(closeTask.IsCompleted);

        // Release Spin -> commits on v1 -> CloseVersionAsync acquires XLOCK and closes v1
        spinResumeGate.SetResult();
        var spinResult = await spinTask.WaitAsync(BarrierTimeout);
        var closedVersion = await closeTask.WaitAsync(BarrierTimeout);

        Assert.Equal("Win", spinResult.Result);
        Assert.Equal("Closed", closedVersion.Status);
        Assert.Equal(1, Volatile.Read(ref closeLockAcquired));

        await using var verify = fixture.CreateContext();
        var savedSpin = await verify.SpinHistories.AsNoTracking().SingleAsync(x => x.WheelId == seed.Wheel.Id);
        Assert.Equal(seed.Version.Id, savedSpin.WheelVersionId);
        var savedVersion = await verify.WheelVersions.AsNoTracking().SingleAsync(x => x.Id == seed.Version.Id);
        Assert.Equal(WheelVersionStatus.Closed, savedVersion.Status);
    }

    [Fact]
    public async Task Race_CloseVersionAcquiresLockBeforeSpin_SpinBlockedThenRejectedAsWheelNotAvailable()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);

        var closeReadyToCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCommitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeSavePause = new SavePauseInterceptor(closeReadyToCommit, closeCommitGate, afterSave: true);

        var spinWaitingOnWheelLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spinWheelReadCompleted = 0;
        var spinObserver = new CommandExecutingObserver(
            cmd => cmd.CommandText.Contains("FROM [Wheels] WITH (REPEATABLEREAD, ROWLOCK)", StringComparison.Ordinal),
            onExecuting: () => spinWaitingOnWheelLock.TrySetResult(),
            onExecuted: () => Interlocked.Exchange(ref spinWheelReadCompleted, 1));

        var adminId = await SeedAdminAsync();
        await using var closeSp = BuildTestServiceProvider(drawValue: 0, adminId: adminId, interceptorsFactory: () => [closeSavePause]);
        await using var spinSp = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [spinObserver]);

        // Admin acquires XLOCK on Wheels, updates v1 to Closed inside tx, pauses right before Commit
        var closeTask = RunCloseVersionInScopeAsync(closeSp, seed.Version.Id);
        await closeReadyToCommit.Task.WaitAsync(BarrierTimeout);

        // Public spin starts and blocks on Wheels S lock while Close holds XLOCK
        var spinTask = RunSpinInScopeAsync(spinSp, seed.Slug, $"sql-close-race2-{Guid.NewGuid():N}", new SpinRequest("lateplayer@gmail.com", true));
        await spinWaitingOnWheelLock.Task.WaitAsync(BarrierTimeout);
        Assert.Equal(0, Volatile.Read(ref spinWheelReadCompleted));
        Assert.False(spinTask.IsCompleted);

        // Release Close commit -> Spin unblocks and observes no active version -> WHEEL_NOT_AVAILABLE
        closeCommitGate.SetResult();
        await closeTask.WaitAsync(BarrierTimeout);
        var ex = await Assert.ThrowsAsync<NotFoundException>(() => spinTask.WaitAsync(BarrierTimeout));

        Assert.Equal("WHEEL_NOT_AVAILABLE", ex.Code);
        Assert.Equal(1, Volatile.Read(ref spinWheelReadCompleted));

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.SpinHistories.CountAsync(x => x.WheelId == seed.Wheel.Id));
        Assert.Equal(0, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id));
        var key = await verify.PrizeKeys.AsNoTracking().SingleAsync(x => x.PrizeId == seed.PrizeA.Id);
        Assert.Equal(PrizeKeyStatus.Available, key.Status);
    }

    [Fact]
    public async Task Race_DisableWheelAndSpin_SerializedCleanlyInBothOrders()
    {
        // Order A: Spin admitted before Disable -> Spin commits, Disable waits on S lock then commits.
        var seedA = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var spinEnteredSelector = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spinResumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingSelector = new BlockingWeightedPrizeSelector(new WeightedPrizeSelector(new TestRandomProvider(0)), spinEnteredSelector, spinResumeGate);

        var disableBlockedOnWheelLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disableUpdateFinished = 0;
        var disableObserver = new CommandExecutingObserver(
            cmd => cmd.CommandText.Contains("UPDATE [Wheels]", StringComparison.Ordinal),
            onExecuting: () => disableBlockedOnWheelLock.TrySetResult(),
            onExecuted: () => Interlocked.Exchange(ref disableUpdateFinished, 1));

        await using var spinSpA = BuildTestServiceProvider(drawValue: 0, customSelector: blockingSelector);
        await using var disableSpA = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [disableObserver]);

        var spinTaskA = RunSpinInScopeAsync(spinSpA, seedA.Slug, $"sql-disable-a-{Guid.NewGuid():N}", new SpinRequest("playerA@gmail.com", true));
        await spinEnteredSelector.Task.WaitAsync(BarrierTimeout);

        var disableTaskA = RunDisableWheelTxAsync(disableSpA, seedA.Wheel.Id, pauseBeforeCommit: null);
        await disableBlockedOnWheelLock.Task.WaitAsync(BarrierTimeout);
        Assert.Equal(0, Volatile.Read(ref disableUpdateFinished));

        spinResumeGate.SetResult();
        var winA = await spinTaskA.WaitAsync(BarrierTimeout);
        await disableTaskA.WaitAsync(BarrierTimeout);
        Assert.Equal("Win", winA.Result);
        Assert.Equal(1, Volatile.Read(ref disableUpdateFinished));

        // Order B: Disable acquires X lock on Wheels and pauses before commit -> Spin blocks, then throws WHEEL_NOT_AVAILABLE.
        var seedB = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var disableReadyToCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disableCommitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disableSavePause = new SavePauseInterceptor(disableReadyToCommit, disableCommitGate, afterSave: true);

        var spinBlockedOnWheel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spinWheelReadDone = 0;
        var spinObserverB = new CommandExecutingObserver(
            cmd => cmd.CommandText.Contains("FROM [Wheels] WITH (REPEATABLEREAD, ROWLOCK)", StringComparison.Ordinal),
            onExecuting: () => spinBlockedOnWheel.TrySetResult(),
            onExecuted: () => Interlocked.Exchange(ref spinWheelReadDone, 1));

        await using var disableSpB = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [disableSavePause]);
        await using var spinSpB = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [spinObserverB]);

        var disableTaskB = RunDisableWheelTxAsync(disableSpB, seedB.Wheel.Id, pauseBeforeCommit: null);
        await disableReadyToCommit.Task.WaitAsync(BarrierTimeout);

        var spinTaskB = RunSpinInScopeAsync(spinSpB, seedB.Slug, $"sql-disable-b-{Guid.NewGuid():N}", new SpinRequest("playerB@gmail.com", true));
        await spinBlockedOnWheel.Task.WaitAsync(BarrierTimeout);
        Assert.Equal(0, Volatile.Read(ref spinWheelReadDone));

        disableCommitGate.SetResult();
        await disableTaskB.WaitAsync(BarrierTimeout);
        var exB = await Assert.ThrowsAsync<NotFoundException>(() => spinTaskB.WaitAsync(BarrierTimeout));
        Assert.Equal("WHEEL_NOT_AVAILABLE", exB.Code);

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.SpinHistories.CountAsync(x => x.WheelId == seedB.Wheel.Id));
    }

    [Fact]
    public async Task Race_ReplacementVersionCloseAndActivate_SpinSerializesOntoNewVersion()
    {
        // Case 1: Single-transaction replacement (XLOCK on Wheel, close v1, activate v2) races with SpinAsync
        var seed = await SeedWheelWithDraftReplacementAsync();

        var replaceReadyToCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replaceCommitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var spinBlockedOnWheel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spinWheelReadDone = 0;
        var spinObserver = new CommandExecutingObserver(
            cmd => cmd.CommandText.Contains("FROM [Wheels] WITH (REPEATABLEREAD, ROWLOCK)", StringComparison.Ordinal),
            onExecuting: () => spinBlockedOnWheel.TrySetResult(),
            onExecuted: () => Interlocked.Exchange(ref spinWheelReadDone, 1));

        await using var replaceSp = BuildTestServiceProvider(drawValue: 0);
        await using var spinSp = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [spinObserver]);

        var replaceTask = RunAtomicVersionReplacementTxAsync(
            replaceSp,
            seed.Wheel.Id,
            seed.Version.Id,
            seed.DraftVersion.Id,
            replaceReadyToCommit,
            replaceCommitGate);
        await replaceReadyToCommit.Task.WaitAsync(BarrierTimeout);

        var spinTask = RunSpinInScopeAsync(spinSp, seed.Slug, $"sql-replace-v2-{Guid.NewGuid():N}", new SpinRequest("replacement@gmail.com", true));
        await spinBlockedOnWheel.Task.WaitAsync(BarrierTimeout);
        Assert.Equal(0, Volatile.Read(ref spinWheelReadDone));

        replaceCommitGate.SetResult();
        await replaceTask.WaitAsync(BarrierTimeout);
        var spinRes = await spinTask.WaitAsync(BarrierTimeout);

        Assert.Equal("Win", spinRes.Result);

        await using (var verify = fixture.CreateContext())
        {
            var savedSpin = await verify.SpinHistories.AsNoTracking().SingleAsync(x => x.Id == spinRes.SpinId);
            Assert.Equal(seed.DraftVersion.Id, savedSpin.WheelVersionId);
        }

        // Case 2: ActivateVersionAsync(v3) via AdminManagementService races with SpinAsync when v2 was already closed
        var seed2 = await SeedWheelWithClosedAndDraftAsync();
        var activateReadyToCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activateCommitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activatePause = new SavePauseInterceptor(activateReadyToCommit, activateCommitGate, afterSave: true);

        var spin2Blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spin2WheelReadDone = 0;
        var spin2Observer = new CommandExecutingObserver(
            cmd => cmd.CommandText.Contains("FROM [Wheels] WITH (REPEATABLEREAD, ROWLOCK)", StringComparison.Ordinal),
            onExecuting: () => spin2Blocked.TrySetResult(),
            onExecuted: () => Interlocked.Exchange(ref spin2WheelReadDone, 1));

        var adminId = await SeedAdminAsync();
        await using var activateSp = BuildTestServiceProvider(drawValue: 0, adminId: adminId, interceptorsFactory: () => [activatePause]);
        await using var spin2Sp = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [spin2Observer]);

        var activateTask = RunActivateVersionInScopeAsync(activateSp, seed2.DraftVersion.Id);
        await activateReadyToCommit.Task.WaitAsync(BarrierTimeout);

        var spin2Task = RunSpinInScopeAsync(spin2Sp, seed2.Slug, $"sql-activate-v2-{Guid.NewGuid():N}", new SpinRequest("activated@gmail.com", true));
        await spin2Blocked.Task.WaitAsync(BarrierTimeout);
        Assert.Equal(0, Volatile.Read(ref spin2WheelReadDone));

        activateCommitGate.SetResult();
        var activatedDto = await activateTask.WaitAsync(BarrierTimeout);
        Assert.Equal("Active", activatedDto.Status);

        var spin2Res = await spin2Task.WaitAsync(BarrierTimeout);
        Assert.Equal("Win", spin2Res.Result);

        await using var verify2 = fixture.CreateContext();
        var savedSpin2 = await verify2.SpinHistories.AsNoTracking().SingleAsync(x => x.Id == spin2Res.SpinId);
        Assert.Equal(seed2.DraftVersion.Id, savedSpin2.WheelVersionId);
    }

    [Fact]
    public async Task Rollback_OnConflict_LeavesCleanAttemptStateWithoutClearingUnrelatedTrackedEntities()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 2, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var unrelatedSeed = await SeedWheelAsync(availableKeyCount: 0, prizeWeight: 0, noPrizeWeight: 1_000_000, singleNoPrizeOnly: true);

        var barrier = new TwoPartySaveBarrier();

        await using var sp1 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty1Interceptor()]);
        await using var sp2 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [barrier.CreateParty2Interceptor()]);

        await using var scope1 = sp1.CreateAsyncScope();
        await using var scope2 = sp2.CreateAsyncScope();

        var db2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var trackedUnrelatedWheel = await db2.Wheels.SingleAsync(x => x.Id == unrelatedSeed.Wheel.Id);
        Assert.Equal(EntityState.Unchanged, db2.Entry(trackedUnrelatedWheel).State);

        var svc1 = scope1.ServiceProvider.GetRequiredService<IPublicWheelService>();
        var svc2 = scope2.ServiceProvider.GetRequiredService<IPublicWheelService>();

        var t1 = svc1.SpinAsync(seed.Slug, $"sql-rb-1-{Guid.NewGuid():N}", new SpinRequest("Quang.Dev+rb1@gmail.com", true));
        var t2 = svc2.SpinAsync(seed.Slug, $"sql-rb-2-{Guid.NewGuid():N}", new SpinRequest("quangdev+rb2@GMAIL.COM", true));

        await barrier.BothReachedSaveAsync.WaitAsync(BarrierTimeout);
        barrier.ReleaseFirst();
        var win1 = await t1.WaitAsync(BarrierTimeout);
        Assert.Equal("Win", win1.Result);

        barrier.ReleaseSecond();
        var conflict = await Assert.ThrowsAsync<ConflictException>(() => t2.WaitAsync(BarrierTimeout));
        Assert.Equal("EMAIL_ALREADY_WON", conflict.Code);

        // Unrelated tracked entity must NOT have been wiped by ChangeTracker.Clear()
        Assert.Equal(EntityState.Unchanged, db2.Entry(trackedUnrelatedWheel).State);
        Assert.Empty(db2.ChangeTracker.Entries<SpinHistory>());
        Assert.Empty(db2.ChangeTracker.Entries<WinnerLock>());
        Assert.Empty(db2.ChangeTracker.Entries<PrizeKey>());
        Assert.Null(db2.Database.CurrentTransaction);

        // A subsequent write on the exact same DbContext must NOT persist dirty entities from the failed attempt
        db2.AuditLogs.Add(new AuditLog(null, AuditAction.Updated, "Wheel", unrelatedSeed.Wheel.Id, "Safe follow-up write", null, _now));
        await db2.SaveChangesAsync();

        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.SpinHistories.CountAsync(x => x.WheelId == seed.Wheel.Id));
        Assert.Equal(1, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id && x.IsActive));
        var keys = await verify.PrizeKeys.AsNoTracking().Where(x => x.PrizeId == seed.PrizeA.Id).ToListAsync();
        Assert.Single(keys, k => k.Status == PrizeKeyStatus.Assigned);
        Assert.Single(keys, k => k.Status == PrizeKeyStatus.Available);
    }

    [Fact]
    public async Task ExecutionStrategyRetry_AfterSimulatedCommitConfirmationFailure_ReplaysWithoutDuplicate()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var commitDropInterceptor = new TransientCommitDropInterceptor();
        var txObserver = new TransactionLifecycleObserver();
        var strategyCounter = new ExecutionAttemptCounter();

        await using var sp = BuildTestServiceProvider(
            drawValue: 0,
            useTestRetryingStrategy: true,
            strategyCounter: strategyCounter,
            interceptorsFactory: () => [txObserver, commitDropInterceptor]);

        var idempKey = $"sql-commit-drop-{Guid.NewGuid():N}";
        var result = await RunSpinInScopeAsync(sp, seed.Slug, idempKey, new SpinRequest("retry.commit@gmail.com", true)).WaitAsync(BarrierTimeout);

        Assert.Equal("Win", result.Result);
        Assert.NotNull(result.Prize?.Key);
        Assert.Equal(1, commitDropInterceptor.TriggeredCount);
        Assert.True(strategyCounter.AttemptCount >= 2);
        Assert.Equal(1, txObserver.StartedCount);
        Assert.Equal(1, txObserver.CommittedCount);

        await using var verify = fixture.CreateContext();
        var spins = await verify.SpinHistories.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id).ToListAsync();
        Assert.Single(spins);
        Assert.Equal(result.SpinId, spins[0].Id);
        Assert.Equal(1, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id && x.IsActive));
        Assert.Equal(1, await verify.PrizeKeys.CountAsync(x => x.PrizeId == seed.PrizeA.Id && x.Status == PrizeKeyStatus.Assigned));
    }

    [Fact]
    public async Task ExecutionStrategyRetry_AfterPreCommitTransientFailure_RetriesCleanlyWithSingleSpinAndKey()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var preCommitDropInterceptor = new TransientPreCommitDropInterceptor();
        var txObserver = new TransactionLifecycleObserver();

        await using var sp = BuildTestServiceProvider(
            drawValue: 0,
            useTestRetryingStrategy: true,
            interceptorsFactory: () => [preCommitDropInterceptor, txObserver]);

        var idempKey = $"sql-precommit-drop-{Guid.NewGuid():N}";
        var result = await RunSpinInScopeAsync(sp, seed.Slug, idempKey, new SpinRequest("retry.precommit@gmail.com", true)).WaitAsync(BarrierTimeout);

        Assert.Equal("Win", result.Result);
        Assert.NotNull(result.Prize?.Key);
        Assert.Equal(1, preCommitDropInterceptor.TriggeredCount);
        Assert.Equal(2, txObserver.StartedCount);
        Assert.Equal(1, txObserver.RolledBackCount);
        Assert.Equal(1, txObserver.CommittedCount);

        await using var verify = fixture.CreateContext();
        var spins = await verify.SpinHistories.AsNoTracking().Where(x => x.WheelId == seed.Wheel.Id).ToListAsync();
        Assert.Single(spins);
        Assert.Equal(result.SpinId, spins[0].Id);
        Assert.Equal(1, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id && x.IsActive));
        Assert.Equal(1, await verify.PrizeKeys.CountAsync(x => x.PrizeId == seed.PrizeA.Id && x.Status == PrizeKeyStatus.Assigned));
    }

    [Fact]
    public async Task NonUniqueDbUpdateException_IsNotMisclassifiedAsIdempotencyOrWinnerLock()
    {
        var seed = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000);
        var sqlFaultInterceptor = new ThrowSqlFaultOnSpinInsertInterceptor();

        await using var sp = BuildTestServiceProvider(
            drawValue: 0,
            interceptorsFactory: () => [sqlFaultInterceptor]);

        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var trackedWheel = await db.Wheels.SingleAsync(x => x.Id == seed.Wheel.Id);
        var service = scope.ServiceProvider.GetRequiredService<IPublicWheelService>();

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.SpinAsync(seed.Slug, $"sql-non-unique-{Guid.NewGuid():N}", new SpinRequest("fault@gmail.com", true)));

        Assert.IsNotType<ConflictException>(ex);
        Assert.Equal(EntityState.Unchanged, db.Entry(trackedWheel).State);
        Assert.Empty(db.ChangeTracker.Entries<SpinHistory>());
        Assert.Empty(db.ChangeTracker.Entries<WinnerLock>());
        Assert.Empty(db.ChangeTracker.Entries<PrizeKey>());
        Assert.Null(db.Database.CurrentTransaction);

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.SpinHistories.CountAsync(x => x.WheelId == seed.Wheel.Id));
        Assert.Equal(0, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id));
        var key = await verify.PrizeKeys.AsNoTracking().SingleAsync(x => x.PrizeId == seed.PrizeA.Id);
        Assert.Equal(PrizeKeyStatus.Available, key.Status);
        Assert.Null(key.AssignedSpinId);
    }

    [Fact]
    public async Task LockWaitPastDeadline_OnWheelLock_RejectsSpinWithoutCreatingSpinKeyOrLock()
    {
        var startAtUtc = _now.AddMinutes(-10);
        var endAtUtc = _now.AddMinutes(5);
        var seed = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000, startAtUtc: startAtUtc, endAtUtc: endAtUtc);
        var clock = new MutableClock(_now); // before EndAtUtc

        var wheelLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWheelLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var spinWaitingOnWheel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wheelReadCompleted = 0;
        var spinObserver = new CommandExecutingObserver(
            cmd => cmd.CommandText.Contains("FROM [Wheels] WITH (REPEATABLEREAD, ROWLOCK)", StringComparison.Ordinal),
            onExecuting: () => spinWaitingOnWheel.TrySetResult(),
            onExecuted: () => Interlocked.Exchange(ref wheelReadCompleted, 1));

        await using var sp = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [spinObserver], customClock: clock);

        // Hold XLOCK on Wheels while keeping Wheel enabled and Version active.
        var holderTask = HoldExclusiveRowLockAsync(
            $"SELECT [IsEnabled] AS [Value] FROM [Wheels] WITH (XLOCK, ROWLOCK) WHERE [Id] = {seed.Wheel.Id}",
            wheelLockAcquired,
            releaseWheelLock);
        await wheelLockAcquired.Task.WaitAsync(BarrierTimeout);

        var spinTask = RunSpinInScopeAsync(sp, seed.Slug, $"sql-stale-wheel-{Guid.NewGuid():N}", new SpinRequest("wheelwait@gmail.com", true));
        await spinWaitingOnWheel.Task.WaitAsync(BarrierTimeout);
        Assert.Equal(0, Volatile.Read(ref wheelReadCompleted));
        Assert.False(spinTask.IsCompleted);

        // Advance clock past EndAtUtc while Spin is blocked on the Wheel lock, then release the lock.
        clock.AdvanceTo(endAtUtc.AddSeconds(1));
        releaseWheelLock.SetResult();
        await holderTask.WaitAsync(BarrierTimeout);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => spinTask.WaitAsync(BarrierTimeout));
        Assert.Equal("WHEEL_NOT_AVAILABLE", ex.Code);
        Assert.Equal(1, Volatile.Read(ref wheelReadCompleted));

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.SpinHistories.CountAsync(x => x.WheelId == seed.Wheel.Id));
        Assert.Equal(0, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id));
        var key = await verify.PrizeKeys.AsNoTracking().SingleAsync(x => x.PrizeId == seed.PrizeA.Id);
        Assert.Equal(PrizeKeyStatus.Available, key.Status);
        Assert.Null(key.AssignedSpinId);
    }

    [Fact]
    public async Task LockWaitPastDeadline_OnWheelVersionLock_RejectsSpinWithoutCreatingSpinKeyOrLock()
    {
        var startAtUtc = _now.AddMinutes(-10);
        var endAtUtc = _now.AddMinutes(5);
        var seed = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000, startAtUtc: startAtUtc, endAtUtc: endAtUtc);
        var clock = new MutableClock(_now); // before EndAtUtc

        var versionLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVersionLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var spinWaitingOnVersion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var versionReadCompleted = 0;
        var spinObserver = new CommandExecutingObserver(
            cmd => cmd.CommandText.Contains("FROM [WheelVersions] WITH (REPEATABLEREAD, ROWLOCK)", StringComparison.Ordinal),
            onExecuting: () => spinWaitingOnVersion.TrySetResult(),
            onExecuted: () => Interlocked.Exchange(ref versionReadCompleted, 1));

        await using var sp = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [spinObserver], customClock: clock);

        // Hold XLOCK ONLY on WheelVersions (Wheel itself is NOT locked), proving WheelVersion wait is also covered.
        var holderTask = HoldExclusiveRowLockAsync(
            $"SELECT [ClaimDurationMinutes] AS [Value] FROM [WheelVersions] WITH (XLOCK, ROWLOCK) WHERE [Id] = {seed.Version.Id}",
            versionLockAcquired,
            releaseVersionLock);
        await versionLockAcquired.Task.WaitAsync(BarrierTimeout);

        var spinTask = RunSpinInScopeAsync(sp, seed.Slug, $"sql-stale-ver-{Guid.NewGuid():N}", new SpinRequest("versionwait@gmail.com", true));
        await spinWaitingOnVersion.Task.WaitAsync(BarrierTimeout);
        Assert.Equal(0, Volatile.Read(ref versionReadCompleted));
        Assert.False(spinTask.IsCompleted);

        // Advance clock past EndAtUtc while Spin is blocked on the WheelVersion lock, then release the lock.
        clock.AdvanceTo(endAtUtc.AddSeconds(1));
        releaseVersionLock.SetResult();
        await holderTask.WaitAsync(BarrierTimeout);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => spinTask.WaitAsync(BarrierTimeout));
        Assert.Equal("WHEEL_NOT_AVAILABLE", ex.Code);
        Assert.Equal(1, Volatile.Read(ref versionReadCompleted));

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.SpinHistories.CountAsync(x => x.WheelId == seed.Wheel.Id));
        Assert.Equal(0, await verify.WinnerLocks.CountAsync(x => x.WheelId == seed.Wheel.Id));
        var key = await verify.PrizeKeys.AsNoTracking().SingleAsync(x => x.PrizeId == seed.PrizeA.Id);
        Assert.Equal(PrizeKeyStatus.Available, key.Status);
        Assert.Null(key.AssignedSpinId);
    }

    [Fact]
    public async Task ValidAdmissionWithWaitBeforeAllocation_KeyExpirationUsesAssignmentTimeAndPreservesAcceptedTermsAtUtc()
    {
        var t0RequestStart = _now;
        var t1AdmissionAfterLockWait = _now.AddMinutes(2);
        var t2KeyAssignment = _now.AddMinutes(4);
        var startAtUtc = _now.AddMinutes(-10);
        var endAtUtc = _now.AddMinutes(5);
        const int claimDurationMinutes = 60;

        var seed = await SeedWheelAsync(
            availableKeyCount: 1,
            prizeWeight: 900_000,
            noPrizeWeight: 100_000,
            startAtUtc: startAtUtc,
            endAtUtc: endAtUtc,
            claimDurationMinutes: claimDurationMinutes);

        var clock = new MutableClock(t0RequestStart);

        var wheelLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWheelLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var spinWaitingOnWheel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spinObserver = new CommandExecutingObserver(
            cmd => cmd.CommandText.Contains("FROM [Wheels] WITH (REPEATABLEREAD, ROWLOCK)", StringComparison.Ordinal),
            onExecuting: () => spinWaitingOnWheel.TrySetResult(),
            onExecuted: () => { });

        var pausedBeforeAllocation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeAllocation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingSelector = new BlockingWeightedPrizeSelector(
            new WeightedPrizeSelector(new TestRandomProvider(0)),
            pausedBeforeAllocation,
            resumeAllocation);

        await using var sp = BuildTestServiceProvider(
            drawValue: 0,
            interceptorsFactory: () => [spinObserver],
            customSelector: blockingSelector,
            customClock: clock);

        // Hold Wheel lock so request waits between t0 and t1, but is still inside [StartAtUtc, EndAtUtc] at admission (t1).
        var holderTask = HoldExclusiveRowLockAsync(
            $"SELECT [IsEnabled] AS [Value] FROM [Wheels] WITH (XLOCK, ROWLOCK) WHERE [Id] = {seed.Wheel.Id}",
            wheelLockAcquired,
            releaseWheelLock);
        await wheelLockAcquired.Task.WaitAsync(BarrierTimeout);

        var spinTask = RunSpinInScopeAsync(sp, seed.Slug, $"sql-alloc-time-{Guid.NewGuid():N}", new SpinRequest("allocwait@gmail.com", true));
        await spinWaitingOnWheel.Task.WaitAsync(BarrierTimeout);

        clock.AdvanceTo(t1AdmissionAfterLockWait);
        releaseWheelLock.SetResult();
        await holderTask.WaitAsync(BarrierTimeout);

        // Admission succeeded at t1; now wait before key allocation while clock advances to t2.
        await pausedBeforeAllocation.Task.WaitAsync(BarrierTimeout);
        clock.AdvanceTo(t2KeyAssignment);
        resumeAllocation.SetResult();

        var result = await spinTask.WaitAsync(BarrierTimeout);
        var expectedExpiresAtUtc = t2KeyAssignment.AddMinutes(claimDurationMinutes);

        Assert.Equal("Win", result.Result);
        Assert.NotNull(result.Prize);
        Assert.Equal(expectedExpiresAtUtc, result.Prize!.KeyExpiresAtUtc);
        Assert.Equal(t2KeyAssignment, result.CompletedAtUtc);

        await using var verify = fixture.CreateContext();
        var spin = await verify.SpinHistories.AsNoTracking().SingleAsync(x => x.Id == result.SpinId);
        Assert.Equal(t0RequestStart, spin.AcceptedTermsAtUtc);
        Assert.Equal(t2KeyAssignment, spin.CreatedAtUtc);

        var key = await verify.PrizeKeys.AsNoTracking().SingleAsync(x => x.Id == spin.PrizeKeyId);
        Assert.Equal(PrizeKeyStatus.Assigned, key.Status);
        Assert.Equal(t2KeyAssignment, key.AssignedAtUtc);
        Assert.Equal(expectedExpiresAtUtc, key.ExpiresAtUtc);

        var winnerLock = await verify.WinnerLocks.AsNoTracking().SingleAsync(x => x.SpinId == spin.Id);
        Assert.Equal(t2KeyAssignment, winnerLock.LockedAtUtc);
    }

    [Fact]
    public async Task Replay_AfterVersionDeadlinePassed_ReturnsOriginalResultsIncludingConcurrentLockWaitRace()
    {
        var startAtUtc = _now.AddMinutes(-10);
        var endAtUtc = _now.AddMinutes(5);
        var seed = await SeedWheelAsync(availableKeyCount: 0, prizeWeight: 900_000, noPrizeWeight: 100_000, startAtUtc: startAtUtc, endAtUtc: endAtUtc);
        var clock = new MutableClock(_now);

        await using var sp = BuildTestServiceProvider(drawValue: 0, customClock: clock);

        // 1. Direct replay after EndAtUtc for both NoPrize fallback and Win
        var npKey = $"sql-deadline-np-{Guid.NewGuid():N}";
        var originalNoPrize = await RunSpinInScopeAsync(sp, seed.Slug, npKey, new SpinRequest("Quang.Dev+np@gmail.com", true)).WaitAsync(BarrierTimeout);
        Assert.Equal("NoPrize", originalNoPrize.Result);

        await using (var replenishDb = fixture.CreateContext())
        {
            var pk = _protector.Protect($"LW-DL-{Guid.NewGuid():N}"[..20]);
            replenishDb.PrizeKeys.Add(new PrizeKey(seed.PrizeA.Id, pk.CodeHash, pk.EncryptedCode, pk.EncryptionNonce, pk.EncryptionTag, _now));
            await replenishDb.SaveChangesAsync();
        }

        clock.AdvanceTo(_now.AddMinutes(1));
        var winKey = $"sql-deadline-win-{Guid.NewGuid():N}";
        var originalWin = await RunSpinInScopeAsync(sp, seed.Slug, winKey, new SpinRequest("quangdev+win@gmail.com", true)).WaitAsync(BarrierTimeout);
        Assert.Equal("Win", originalWin.Result);

        // Move clock past EndAtUtc -> replay must still succeed
        clock.AdvanceTo(endAtUtc.AddMinutes(30));

        var replayNoPrize = await RunSpinInScopeAsync(sp, seed.Slug, npKey, new SpinRequest("q.u.a.n.g.d.e.v+retry1@GMAIL.COM", true)).WaitAsync(BarrierTimeout);
        var replayWin = await RunSpinInScopeAsync(sp, seed.Slug, winKey, new SpinRequest("Quang.Dev+retry2@gmail.com", true)).WaitAsync(BarrierTimeout);

        Assert.Equal(originalNoPrize.SpinId, replayNoPrize.SpinId);
        Assert.Equal(originalNoPrize.ReceiptToken, replayNoPrize.ReceiptToken);
        Assert.Equal(originalWin.SpinId, replayWin.SpinId);
        Assert.Equal(originalWin.ReceiptToken, replayWin.ReceiptToken);
        Assert.Equal(originalWin.Prize!.Key, replayWin.Prize!.Key);
        Assert.Equal(originalWin.Prize.KeyExpiresAtUtc, replayWin.Prize.KeyExpiresAtUtc);

        // 2. Concurrent race: Request 2 misses initial replay check, waits past EndAtUtc while Request 1 commits,
        //    then rejects admission at EndAtUtc and must still replay Request 1 via post-rollback idempotency resolution.
        var raceSeed = await SeedWheelAsync(availableKeyCount: 1, prizeWeight: 900_000, noPrizeWeight: 100_000, startAtUtc: startAtUtc, endAtUtc: endAtUtc);
        var raceClock = new MutableClock(_now);
        var raceIdempKey = $"sql-deadline-race-{Guid.NewGuid():N}";

        var r2PausedBeforeWheelLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r2ResumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r2Interceptor = new CommandExecutingPauseInterceptor(
            cmd => cmd.CommandText.Contains("FROM [Wheels] WITH (REPEATABLEREAD, ROWLOCK)", StringComparison.Ordinal),
            r2PausedBeforeWheelLock,
            r2ResumeGate);

        await using var sp1 = BuildTestServiceProvider(drawValue: 0, customClock: raceClock);
        await using var sp2 = BuildTestServiceProvider(drawValue: 0, interceptorsFactory: () => [r2Interceptor], customClock: raceClock);

        var r2Task = RunSpinInScopeAsync(sp2, raceSeed.Slug, raceIdempKey, new SpinRequest("race.deadline+2@gmail.com", true));
        await r2PausedBeforeWheelLock.Task.WaitAsync(BarrierTimeout);

        // Request 1 commits Win before EndAtUtc
        raceClock.AdvanceTo(_now.AddMinutes(1));
        var r1Win = await RunSpinInScopeAsync(sp1, raceSeed.Slug, raceIdempKey, new SpinRequest("racedeadline+1@gmail.com", true)).WaitAsync(BarrierTimeout);
        Assert.Equal("Win", r1Win.Result);

        // Clock crosses EndAtUtc before Request 2 acquires Wheel/Version locks
        raceClock.AdvanceTo(endAtUtc.AddMinutes(5));
        r2ResumeGate.SetResult();

        var r2Replay = await r2Task.WaitAsync(BarrierTimeout);
        Assert.Equal(r1Win.SpinId, r2Replay.SpinId);
        Assert.Equal(r1Win.ReceiptToken, r2Replay.ReceiptToken);
        Assert.Equal(r1Win.Prize!.Key, r2Replay.Prize!.Key);

        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.SpinHistories.CountAsync(x => x.WheelId == raceSeed.Wheel.Id));
        Assert.Equal(1, await verify.WinnerLocks.CountAsync(x => x.WheelId == raceSeed.Wheel.Id && x.IsActive));
        Assert.Equal(1, await verify.PrizeKeys.CountAsync(x => x.PrizeId == raceSeed.PrizeA.Id && x.Status == PrizeKeyStatus.Assigned));
    }

    // ── Seed & Scope Helpers ──────────────────────────────────────────────────────────────

    private static IPrizeKeyProtector CreateProtector()
    {
        var keyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new AesGcmPrizeKeyProtector(Options.Create(new PrizeKeyProtectionOptions { EncryptionKey = keyBase64 }));
    }

    private ServiceProvider BuildTestServiceProvider(
        long drawValue,
        Func<IInterceptor[]>? interceptorsFactory = null,
        IWeightedPrizeSelector? customSelector = null,
        IPrizeKeyProtector? customProtector = null,
        bool useTestRetryingStrategy = false,
        ExecutionAttemptCounter? strategyCounter = null,
        Guid? adminId = null,
        IClock? customClock = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(customClock ?? new FixedClock(_now));
        services.AddSingleton<ICurrentAdminContext>(new TestAdminContext(adminId ?? Guid.NewGuid()));
        services.AddSingleton<IRandomNumberProvider>(new TestRandomProvider(drawValue));
        services.AddSingleton(customProtector ?? _protector);
        services.AddScoped<IWeightedPrizeSelector>(sp =>
            customSelector ?? new WeightedPrizeSelector(sp.GetRequiredService<IRandomNumberProvider>()));

        services.AddDbContext<ApplicationDbContext>((_, options) =>
        {
            options.UseSqlServer(fixture.ConnectionString, sqlOptions =>
            {
                sqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                if (useTestRetryingStrategy)
                {
                    sqlOptions.ExecutionStrategy(deps => new TestRetryingExecutionStrategy(deps, strategyCounter));
                }
                else
                {
                    sqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: null);
                }
            });

            var interceptors = interceptorsFactory?.Invoke();
            if (interceptors is { Length: > 0 })
            {
                options.AddInterceptors(interceptors);
            }
        });

        services.AddScoped<IPublicWheelService, PublicWheelService>();
        services.AddScoped<IAdminManagementService, AdminManagementService>();

        return services.BuildServiceProvider();
    }

    private static async Task<SpinResultDto> RunSpinInScopeAsync(
        ServiceProvider sp,
        string slug,
        string idempotencyKey,
        SpinRequest request)
    {
        await using var scope = sp.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IPublicWheelService>();
        return await service.SpinAsync(slug, idempotencyKey, request);
    }

    private async Task<WheelVersionDto> RunCloseVersionInScopeAsync(ServiceProvider sp, Guid versionId)
    {
        string rowVersion;
        await using (var rvDb = fixture.CreateContext())
        {
            var version = await rvDb.WheelVersions.SingleAsync(x => x.Id == versionId);
            rowVersion = Convert.ToBase64String(rvDb.Entry(version).Property<byte[]>("RowVersion").CurrentValue!);
        }

        await using var scope = sp.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAdminManagementService>();
        return await service.CloseVersionAsync(versionId, new CloseActiveWheelVersionRequest(rowVersion), default);
    }

    private async Task<WheelVersionDto> RunActivateVersionInScopeAsync(ServiceProvider sp, Guid versionId)
    {
        string rowVersion;
        await using (var rvDb = fixture.CreateContext())
        {
            var version = await rvDb.WheelVersions.SingleAsync(x => x.Id == versionId);
            rowVersion = Convert.ToBase64String(rvDb.Entry(version).Property<byte[]>("RowVersion").CurrentValue!);
        }

        await using var scope = sp.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAdminManagementService>();
        return await service.ActivateVersionAsync(versionId, new ActivateDraftWheelVersionRequest(rowVersion), default);
    }

    private async Task RunDisableWheelTxAsync(ServiceProvider sp, Guid wheelId, TaskCompletionSource? pauseBeforeCommit)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var wheel = await db.Wheels.SingleAsync(x => x.Id == wheelId);
            wheel.Disable(_now.AddMinutes(1));
            await db.SaveChangesAsync();
            if (pauseBeforeCommit != null)
            {
                await pauseBeforeCommit.Task;
            }
            await tx.CommitAsync();
        });
    }

    private async Task RunAtomicVersionReplacementTxAsync(
        ServiceProvider sp,
        Guid wheelId,
        Guid activeVersionId,
        Guid draftVersionId,
        TaskCompletionSource readyToCommit,
        TaskCompletionSource commitGate)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var adminId = Guid.NewGuid();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await db.Database
                .SqlQuery<bool>($"SELECT [IsEnabled] AS [Value] FROM [Wheels] WITH (XLOCK, ROWLOCK) WHERE [Id] = {wheelId}")
                .FirstOrDefaultAsync();

            var active = await db.WheelVersions.SingleAsync(x => x.Id == activeVersionId);
            active.Close(_now.AddMinutes(1));
            await db.SaveChangesAsync();

            var draft = await db.WheelVersions.SingleAsync(x => x.Id == draftVersionId);
            draft.Activate(adminId, _now.AddMinutes(1));
            await db.SaveChangesAsync();

            readyToCommit.TrySetResult();
            await commitGate.Task.WaitAsync(BarrierTimeout);
            await tx.CommitAsync();
        });
    }

    private async Task HoldExclusiveRowLockAsync(
        FormattableString lockSql,
        TaskCompletionSource lockAcquired,
        TaskCompletionSource releaseLock)
    {
        await using var db = fixture.CreateContext(enableRetryOnFailure: false);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(lockSql);
        lockAcquired.TrySetResult();
        await releaseLock.Task.WaitAsync(BarrierTimeout);
        await tx.CommitAsync();
    }

    private async Task<Guid> SeedAdminAsync()
    {
        await using var db = fixture.CreateContext();
        var admin = new AdminUser($"admin-{Guid.NewGuid():N}@luckywheel.test", "Concurrency Admin", _now);
        db.AdminUsers.Add(admin);
        await db.SaveChangesAsync();
        return admin.Id;
    }

    private async Task<SeededWheel> SeedWheelAsync(
        int availableKeyCount,
        int prizeWeight,
        int noPrizeWeight,
        bool singleNoPrizeOnly = false,
        DateTime? startAtUtc = null,
        DateTime? endAtUtc = null,
        int claimDurationMinutes = 60)
    {
        await using var db = fixture.CreateContext();
        var slug = $"sql-wheel-{Guid.NewGuid():N}";
        var wheel = new Wheel($"Wheel {slug}", slug, "Desc", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(
            wheel.Id,
            1,
            startAtUtc ?? _now.AddDays(-1),
            endAtUtc ?? _now.AddDays(7),
            claimDurationMinutes,
            _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prizeA = new Prize(wheel.Id, "Prize A", "Voucher", null, true, Math.Max(availableKeyCount, 1), _now);
        db.Prizes.Add(prizeA);

        for (var i = 0; i < availableKeyCount; i++)
        {
            var pk = _protector.Protect($"LW-SQL-{Guid.NewGuid():N}"[..20]);
            db.PrizeKeys.Add(new PrizeKey(prizeA.Id, pk.CodeHash, pk.EncryptedCode, pk.EncryptionNonce, pk.EncryptionTag, _now.AddSeconds(i)));
        }

        WheelVersionPrize noPrizeSeg;
        if (singleNoPrizeOnly)
        {
            noPrizeSeg = new WheelVersionPrize(version.Id, null, 1_000_000, 1, "#CCCCCC", null, true, _now);
            db.WheelVersionPrizes.Add(noPrizeSeg);
        }
        else
        {
            var segA = new WheelVersionPrize(version.Id, prizeA.Id, prizeWeight, 1, "#FFAA00", null, false, _now);
            noPrizeSeg = new WheelVersionPrize(version.Id, null, noPrizeWeight, 2, "#CCCCCC", null, true, _now);
            db.WheelVersionPrizes.AddRange(segA, noPrizeSeg);
        }

        await db.SaveChangesAsync();
        return new SeededWheel(slug, wheel, version, prizeA, null, noPrizeSeg, version);
    }

    private async Task<SeededWheel> SeedWheelWithTwoPrizesAsync(int prizeAKeys, int prizeBKeys)
    {
        await using var db = fixture.CreateContext();
        var slug = $"sql-wheel2-{Guid.NewGuid():N}";
        var wheel = new Wheel($"Wheel {slug}", slug, "Desc", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prizeA = new Prize(wheel.Id, "Prize A", "Voucher A", null, true, Math.Max(prizeAKeys, 1), _now);
        var prizeB = new Prize(wheel.Id, "Prize B", "Voucher B", null, true, Math.Max(prizeBKeys, 1), _now);
        db.Prizes.AddRange(prizeA, prizeB);

        for (var i = 0; i < prizeAKeys; i++)
        {
            var pk = _protector.Protect($"LW-PA-{Guid.NewGuid():N}"[..20]);
            db.PrizeKeys.Add(new PrizeKey(prizeA.Id, pk.CodeHash, pk.EncryptedCode, pk.EncryptionNonce, pk.EncryptionTag, _now.AddSeconds(i)));
        }

        for (var i = 0; i < prizeBKeys; i++)
        {
            var pk = _protector.Protect($"LW-PB-{Guid.NewGuid():N}"[..20]);
            db.PrizeKeys.Add(new PrizeKey(prizeB.Id, pk.CodeHash, pk.EncryptedCode, pk.EncryptionNonce, pk.EncryptionTag, _now.AddSeconds(i)));
        }

        var segA = new WheelVersionPrize(version.Id, prizeA.Id, 700_000, 1, "#FF0000", null, false, _now);
        var segB = new WheelVersionPrize(version.Id, prizeB.Id, 200_000, 2, "#00FF00", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 100_000, 3, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(segA, segB, noPrizeSeg);

        await db.SaveChangesAsync();
        return new SeededWheel(slug, wheel, version, prizeA, prizeB, noPrizeSeg, version);
    }

    private async Task<SeededWheel> SeedWheelWithDraftReplacementAsync()
    {
        await using var db = fixture.CreateContext();
        var slug = $"sql-rep-{Guid.NewGuid():N}";
        var wheel = new Wheel($"Wheel {slug}", slug, "Desc", "Terms", _now);
        db.Wheels.Add(wheel);

        var prize = new Prize(wheel.Id, "Prize A", "Voucher", null, true, 5, _now);
        db.Prizes.Add(prize);
        var pk = _protector.Protect($"LW-REP-{Guid.NewGuid():N}"[..20]);
        db.PrizeKeys.Add(new PrizeKey(prize.Id, pk.CodeHash, pk.EncryptedCode, pk.EncryptionNonce, pk.EncryptionTag, _now));

        var v1 = new WheelVersion(wheel.Id, 1, _now.AddDays(-2), _now.AddDays(7), 60, _now);
        v1.Activate(Guid.NewGuid(), _now);
        var v2 = new WheelVersion(wheel.Id, 2, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        db.WheelVersions.AddRange(v1, v2);

        var noPrizeV1 = new WheelVersionPrize(v1.Id, null, 100_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(v1.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now),
            noPrizeV1,
            new WheelVersionPrize(v2.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(v2.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));

        await db.SaveChangesAsync();
        return new SeededWheel(slug, wheel, v1, prize, null, noPrizeV1, v2);
    }

    private async Task<SeededWheel> SeedWheelWithClosedAndDraftAsync()
    {
        await using var db = fixture.CreateContext();
        var slug = $"sql-act-{Guid.NewGuid():N}";
        var wheel = new Wheel($"Wheel {slug}", slug, "Desc", "Terms", _now);
        db.Wheels.Add(wheel);

        var prize = new Prize(wheel.Id, "Prize A", "Voucher", null, true, 5, _now);
        db.Prizes.Add(prize);
        var pk = _protector.Protect($"LW-ACT-{Guid.NewGuid():N}"[..20]);
        db.PrizeKeys.Add(new PrizeKey(prize.Id, pk.CodeHash, pk.EncryptedCode, pk.EncryptionNonce, pk.EncryptionTag, _now));

        var v1 = new WheelVersion(wheel.Id, 1, _now.AddDays(-2), _now.AddDays(7), 60, _now);
        v1.Activate(Guid.NewGuid(), _now);
        v1.Close(_now);
        var v2 = new WheelVersion(wheel.Id, 2, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        db.WheelVersions.AddRange(v1, v2);

        var noPrizeV1 = new WheelVersionPrize(v1.Id, null, 100_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(v1.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now),
            noPrizeV1,
            new WheelVersionPrize(v2.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(v2.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));

        await db.SaveChangesAsync();
        return new SeededWheel(slug, wheel, v1, prize, null, noPrizeV1, v2);
    }

    private sealed record SeededWheel(
        string Slug,
        Wheel Wheel,
        WheelVersion Version,
        Prize PrizeA,
        Prize? PrizeB,
        WheelVersionPrize NoPrizeSegment,
        WheelVersion DraftVersion);

    // ── Deterministic Synchronization Helpers ────────────────────────────────────────────────

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class MutableClock(DateTime initialUtcNow) : IClock
    {
        private long _utcTicks = DateTime.SpecifyKind(initialUtcNow, DateTimeKind.Utc).Ticks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void AdvanceTo(DateTime utcNow)
            => Interlocked.Exchange(ref _utcTicks, DateTime.SpecifyKind(utcNow, DateTimeKind.Utc).Ticks);
    }

    private sealed class TestAdminContext(Guid adminId) : ICurrentAdminContext
    {
        public Guid? AdminId { get; } = adminId;
        public bool IsAuthenticated => true;
    }

    private sealed class TestRandomProvider(long drawValue) : IRandomNumberProvider
    {
        public int GetInt32(int fromInclusive, int toExclusive) => (int)drawValue;
        public long GetInt64(long fromInclusive, long toExclusive) => drawValue;

        public byte[] GetBytes(int count)
        {
            var bytes = new byte[count];
            RandomNumberGenerator.Fill(bytes);
            return bytes;
        }
    }

    private sealed class TwoPartySaveBarrier
    {
        private readonly TaskCompletionSource _party1Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _party2Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task BothReachedSaveAsync => Task.WhenAll(_party1Reached.Task, _party2Reached.Task);
        public void ReleaseFirst() => _releaseFirst.TrySetResult();
        public void ReleaseSecond() => _releaseSecond.TrySetResult();

        public IInterceptor CreateParty1Interceptor() => new BarrierInterceptor(_party1Reached, _releaseFirst);
        public IInterceptor CreateParty2Interceptor() => new BarrierInterceptor(_party2Reached, _releaseSecond);

        private sealed class BarrierInterceptor(
            TaskCompletionSource reached,
            TaskCompletionSource releaseGate) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                reached.TrySetResult();
                await releaseGate.Task.WaitAsync(BarrierTimeout, cancellationToken);
                return await base.SavingChangesAsync(eventData, result, cancellationToken);
            }
        }
    }

    private sealed class SavePauseInterceptor(
        TaskCompletionSource reached,
        TaskCompletionSource gate,
        bool afterSave) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!afterSave)
            {
                reached.TrySetResult();
                await gate.Task.WaitAsync(BarrierTimeout, cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (afterSave)
            {
                reached.TrySetResult();
                await gate.Task.WaitAsync(BarrierTimeout, cancellationToken);
            }

            return await base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CommandPauseInterceptor(
        Func<DbCommand, bool> shouldPauseAfterExecute,
        TaskCompletionSource onPaused,
        TaskCompletionSource waitGate) : DbCommandInterceptor
    {
        private int _pausedOnce;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (shouldPauseAfterExecute(command) && Interlocked.Exchange(ref _pausedOnce, 1) == 0)
            {
                onPaused.TrySetResult();
                await waitGate.Task.WaitAsync(BarrierTimeout, cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CommandExecutingPauseInterceptor(
        Func<DbCommand, bool> shouldPauseBeforeExecute,
        TaskCompletionSource onPaused,
        TaskCompletionSource waitGate) : DbCommandInterceptor
    {
        private int _pausedOnce;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (shouldPauseBeforeExecute(command) && Interlocked.Exchange(ref _pausedOnce, 1) == 0)
            {
                onPaused.TrySetResult();
                await waitGate.Task.WaitAsync(BarrierTimeout, cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CommandExecutingObserver(
        Func<DbCommand, bool> match,
        Action onExecuting,
        Action onExecuted) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (match(command))
            {
                onExecuting();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (match(command))
            {
                onExecuted();
            }

            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (match(command))
            {
                onExecuting();
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (match(command))
            {
                onExecuted();
            }

            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class BlockingWeightedPrizeSelector(
        IWeightedPrizeSelector inner,
        TaskCompletionSource entered,
        TaskCompletionSource release) : IWeightedPrizeSelector
    {
        public WheelVersionPrize Select(IReadOnlyList<WheelVersionPrize> eligibleCandidates)
        {
            entered.TrySetResult();
            release.Task.Wait(BarrierTimeout);
            return inner.Select(eligibleCandidates);
        }
    }

    private sealed class BlockingPrizeKeyProtector(
        IPrizeKeyProtector inner,
        TaskCompletionSource entered,
        TaskCompletionSource release) : IPrizeKeyProtector
    {
        private int _blockedOnce;

        public string Normalize(string plaintextCode) => inner.Normalize(plaintextCode);
        public ProtectedPrizeKey Protect(string plaintextCode) => inner.Protect(plaintextCode);
        public string ComputeHash(string plaintextCode) => inner.ComputeHash(plaintextCode);

        public string Unprotect(byte[] encryptedCode, byte[] nonce, byte[] tag)
        {
            if (Interlocked.Exchange(ref _blockedOnce, 1) == 0)
            {
                entered.TrySetResult();
                release.Task.Wait(BarrierTimeout);
            }

            return inner.Unprotect(encryptedCode, nonce, tag);
        }
    }

    private sealed class SimulatedTransientCommitException()
        : Exception("Simulated transient network drop after SQL commit confirmation.");

    private sealed class TransientCommitDropInterceptor : DbTransactionInterceptor
    {
        private int _triggered;
        public int TriggeredCount => Volatile.Read(ref _triggered);

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
            if (Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                throw new SimulatedTransientCommitException();
            }
        }
    }

    private sealed class TransientPreCommitDropInterceptor : SaveChangesInterceptor
    {
        private int _triggered;
        public int TriggeredCount => Volatile.Read(ref _triggered);

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                throw new SimulatedTransientCommitException();
            }

            return await base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class TransactionLifecycleObserver : DbTransactionInterceptor
    {
        private int _started;
        private int _rolledBack;
        private int _committed;

        public int StartedCount => Volatile.Read(ref _started);
        public int RolledBackCount => Volatile.Read(ref _rolledBack);
        public int CommittedCount => Volatile.Read(ref _committed);

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);
            return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
        }

        public override Task TransactionRolledBackAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _rolledBack);
            return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _committed);
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    private sealed class ExecutionAttemptCounter
    {
        private int _attemptCount;
        public int AttemptCount => Volatile.Read(ref _attemptCount);
        public void Increment() => Interlocked.Increment(ref _attemptCount);
    }

    private sealed class TestRetryingExecutionStrategy(
        ExecutionStrategyDependencies dependencies,
        ExecutionAttemptCounter? counter)
        : SqlServerRetryingExecutionStrategy(dependencies, maxRetryCount: 5, maxRetryDelay: TimeSpan.FromMilliseconds(50), errorNumbersToAdd: null)
    {
        public override async Task<TResult> ExecuteAsync<TState, TResult>(
            TState state,
            Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
            Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>? verifySucceeded,
            CancellationToken cancellationToken = default)
        {
            return await base.ExecuteAsync(
                state,
                (ctx, s, ct) =>
                {
                    counter?.Increment();
                    return operation(ctx, s, ct);
                },
                verifySucceeded,
                cancellationToken);
        }

        protected override bool ShouldRetryOn(Exception exception)
            => exception is SimulatedTransientCommitException || base.ShouldRetryOn(exception);
    }

    private sealed class ThrowSqlFaultOnSpinInsertInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            InjectSqlFaultIfSpinInsert(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            InjectSqlFaultIfSpinInsert(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void InjectSqlFaultIfSpinInsert(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO [SpinHistories]", StringComparison.Ordinal))
            {
                command.CommandText = "THROW 50001, 'Simulated non-unique SQL Server failure.', 1;";
                command.Parameters.Clear();
            }
        }
    }
}

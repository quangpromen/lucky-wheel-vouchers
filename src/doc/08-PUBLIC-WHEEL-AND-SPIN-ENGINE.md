# Giai đoạn 8: Public Wheel & Spin Engine

## 1. Mục tiêu và phạm vi

- **Mục tiêu:** Triển khai luồng quay thưởng công khai (`Public Wheel & Spin Engine`) cho người dùng tham gia quay may mắn với Gmail, đảm bảo tính đúng đắn nghiệp vụ, giao dịch nguyên tử (atomic transaction), chống race condition / tranh chấp khóa thưởng, xử lý idempotent chặt chẽ và bảo vệ tuyệt đối thông tin nhạy cảm (`PrizeKey`).
- **Phạm vi đã thực hiện:**
    - `GET /api/public/wheels/{slug}`: Lấy thông tin vòng quay active theo slug, trả thông tin vòng quay (`wheelId`, `name`, `slug`, `terms`), cửa sổ hiệu lực của phiên bản active (`versionId`, `versionNumber`, `startAtUtc`, `endAtUtc`) và danh sách `entries` (`id`, `displayOrder`, `label`, `color`, `imageUrl`, `isNoPrize`); chỉ trả vòng quay khả dụng theo contract hiện hành (không mở rộng trạng thái upcoming/ended) và tuyệt đối không để lộ weight, tồn kho, key chưa cấp, email hoặc dữ liệu quản trị.
    - `POST /api/public/wheels/{slug}/spins`: API quay thưởng công khai với header `Idempotency-Key` và payload `{ email, acceptedTerms }`.
    - Quy tắc xác thực và chuẩn hóa Gmail theo `LUCKY_WHEEL_BUSINESS_RULES.md` (§8): kiểm tra Gmail hợp lệ trước khi chuẩn hóa, trim whitespace, lowercase, chỉ chấp nhận domain `@gmail.com`, loại bỏ phần `+tag` và dấu chấm (`.`) trong local part; lưu `EmailOriginal` cho lịch sử và dùng `EmailNormalized` cho eligibility, `WinnerLock` và so sánh idempotency.
    - Khóa trúng thưởng `WinnerLock` theo cặp `(WheelId, EmailNormalized)`: người đã trúng giải (dù ở phiên bản nào) sẽ không được quay trúng thêm giải trên cùng một Wheel.
    - Thuật toán quay thưởng theo trọng số `WeightedPrizeSelector` sử dụng CSPRNG (`CryptoRandomNumberProvider` dựa trên `RandomNumberGenerator`), luôn bốc thăm trên toàn bộ phân khúc hợp lệ của phiên bản Active theo thang cố định `1,000,000` (không loại giải hết mã trước khi quay và không phân phối lại xác suất).
    - Phân bổ `PrizeKey` có điều kiện đồng thời cao: sử dụng SQL Server hint `UPDLOCK, READPAST, ROWLOCK` để chọn đúng 1 key `Available`, chuyển trạng thái sang `Assigned` và mã hóa/giải mã an toàn trong bộ nhớ; nếu giải được chọn không cấp được key `Available`, kết quả chuyển về luồng lưu/trả `NoPrize` dùng đúng phân khúc `NoPrize` của phiên bản đó (không tạo `WinnerLock`, không sửa key, trả HTTP 200).
    - Idempotency & Replay: Lưu trữ `IdempotencyKey` trong bảng `SpinHistories` (`IX_SpinHistories_IdempotencyKey` unique). Kiểm tra idempotency chạy ở đầu mỗi lần thử của `ExecutionStrategy` và được ưu tiên sau khi rollback mọi xung đột/trạng thái đóng trong transaction. Nếu người chơi gửi lại cùng `Idempotency-Key`, cùng `WheelId` và cùng `EmailNormalized` (bao gồm các biến thể cùng Gmail hợp lệ), hệ thống trả lại kết quả gốc đã lưu (kể cả khi Wheel đã đóng/vô hiệu hóa hoặc Gmail đã có `WinnerLock` active) mà không tiêu tốn thêm key hay tạo lượt quay mới. Nếu tái sử dụng `Idempotency-Key` với Wheel hoặc `EmailNormalized` khác, trả về HTTP 409 `IDEMPOTENCY_KEY_REUSED` và không làm rò rỉ dữ liệu gốc.
    - Tính nguyên tử & đồng thời (Bước 3): Sử dụng row-level shared lock (`WITH (REPEATABLEREAD, ROWLOCK)`) trên `Wheels` và `WheelVersions` khi quay, phối hợp với `WITH (XLOCK, ROWLOCK)` trên `Wheels` ở `CloseVersionAsync`/`ActivateVersionAsync`; xác định điểm admission bằng thời gian UTC mới ngay sau khi lấy đủ khóa `Wheel`/`WheelVersion` và tính `AssignedAtUtc`/`ExpiresAtUtc` theo thời điểm gán key (giữ nguyên `AcceptedTermsAtUtc` tại thời điểm nhận/xác thực request); phân bổ key bằng `UPDLOCK, READPAST, ROWLOCK` trên `PrizeKeys`; rollback + dispose transaction lỗi và dọn sạch state cục bộ của attempt trước khi resolve lại idempotency hoặc rethrow cho `ExecutionStrategy`.
    - Public API Response & Snapshot thông tin giải (Bước 4): Lưu snapshot `PrizeNameSnapshot` và `PrizeDescriptionSnapshot` trực tiếp trên `SpinHistory` trong cùng transaction tạo `Win` (`NoPrize` không có snapshot); replay và lịch sử admin đọc từ snapshot thay vì bảng `Prizes` hiện tại, không backfill dữ liệu cũ và áp dụng fallback trung tính cố định `"Phần thưởng"` / `null` cho các `Win` cũ trước migration thiếu snapshot.
    - Rate Limiting tích hợp của ASP.NET Core (.NET 8 built-in `FixedWindowRateLimiter`) chống spam và bruteforce trên endpoint spin.
    - Trạng thái Giai đoạn 8: **Đang triển khai (IN PROGRESS — chưa đánh dấu `COMPLETED`)**. Đã hoàn thiện Bước 1 (chuẩn hóa Gmail), Bước 2 (thang `1,000,000` + fallback `NoPrize`), Bước 3 (idempotency đồng thời + tính nguyên tử Spin), và Bước 4 (hoàn thiện public wheel response & snapshot thông tin giải); các phần còn lại (như cooldown, trường hướng dẫn nhận thưởng khi domain bổ sung nguồn dữ liệu chính thức, và kiểm tra hoàn tất toàn Giai đoạn 8) tiếp tục thực hiện theo lộ trình.

---

## 2. Luồng xử lý giao dịch nguyên tử (Atomic Spin Transaction)

```mermaid
sequenceDiagram
    autonumber
    actor Player as Người chơi
    participant Api as PublicWheelsController
    participant Limiter as RateLimiter Middleware
    participant Engine as PublicWheelService
    participant DB as SQL Server Database

    Player->>Api: POST /api/public/wheels/{slug}/spins (Idempotency-Key, Email, AcceptedTerms)
    Api->>Limiter: Kiểm tra Rate Limit (IP partition)
    Limiter-->>Api: Cho phép (hoặc 429 TOO_MANY_REQUESTS)
    Api->>Engine: SpinAsync(slug, idempotencyKey, request)

    Engine->>Engine: Validate request (cú pháp & domain @gmail.com, safe key format, acceptedTerms == true)

    loop ExecutionStrategy (SqlServerRetryingExecutionStrategy)
        Engine->>DB: TryResolveIdempotencyAsync (AsNoTracking, ưu tiên trước Wheel/Version/WinnerLock)
        alt Tìm thấy SpinHistory cùng IdempotencyKey
            alt Khớp WheelId & EmailNormalized
                Engine-->>Api: Replay SpinResultDto gốc (Decrypt key in-memory nếu Win)
            else Khác WheelId hoặc EmailNormalized
                Engine-->>Api: Ném ConflictException("IDEMPOTENCY_KEY_REUSED")
            end
        else Chưa có bản ghi committed
            Engine->>DB: Bắt đầu Transaction (ReadCommitted)
            Engine->>DB: Khóa shared row: Wheels & Active WheelVersion WITH (REPEATABLEREAD, ROWLOCK)
            Engine->>Engine: Điểm admission: lấy UTC mới sau khi có đủ khóa và kiểm tra StartAtUtc <= admittedAtUtc <= EndAtUtc
            Engine->>DB: Kiểm tra WinnerLock(WheelId, EmailNormalized, IsActive == true)
            alt Wheel/Version không khả dụng (hoặc quá lịch), đã thắng, hoặc DbUpdateException (2601/2627)
                Engine->>DB: Rollback & Dispose Transaction lỗi
                Engine->>Engine: CleanupAttemptState (revert PrizeKey & detach entity của attempt)
                Engine->>DB: Đọc lại TryResolveIdempotencyAsync ngoài transaction lỗi
                alt Tìm thấy kết quả đã commit cùng key
                    Engine-->>Api: Replay hoặc IDEMPOTENCY_KEY_REUSED
                else Không có spin cùng key
                    Engine-->>Api: WHEEL_NOT_AVAILABLE / EMAIL_ALREADY_WON / CONFLICT
                end
            else Hợp lệ -> Bốc thăm & lưu
                Engine->>DB: WeightedPrizeSelector (thang 1,000,000)
                alt Trúng giải RequiresKey = true
                    Engine->>DB: SELECT TOP 1 FROM PrizeKeys WITH (UPDLOCK, READPAST, ROWLOCK)
                    alt Cấp được PrizeKey Available
                        Engine->>Engine: Unprotect key in-memory; lấy assignedAtUtc mới, tính ExpiresAtUtc từ assignedAtUtc; tạo Win Spin (kèm snapshot tên/mô tả Prize, giữ AcceptedTermsAtUtc gốc) + WinnerLock + Assign key
                    else Hết key Available (Fallback NoPrize)
                        Engine->>Engine: Tạo NoPrize Spin với NoPrizeSegment (không lock, không đổi key, không snapshot giải)
                    end
                else Giải RequiresKey = false hoặc trúng ô NoPrize
                    Engine->>Engine: Tạo Win Spin (kèm snapshot tên/mô tả Prize) + WinnerLock hoặc NoPrize Spin
                end
                Engine->>DB: SaveChangesAsync(acceptAllChangesOnSuccess: false) -> CommitAsync -> AcceptAllChanges()
                Engine-->>Api: Trả HTTP 200 SpinResultDto
            end
        end
    end
```

---

## 3. Các quy tắc nghiệp vụ cốt lõi

### 3.1 Quy tắc Gmail

- Email người chơi phải là địa chỉ Gmail hợp lệ trước khi chuẩn hóa và chỉ chấp nhận domain `@gmail.com` (không chấp nhận domain khác như `yahoo.com`, `gmail.com.vn`, `googlemail.com`, hay local part sai cú pháp như dấu chấm ở đầu/cuối, `..`, thiếu phần chính trước `+`, hoặc `+` rỗng).
- Chuẩn hóa theo `LUCKY_WHEEL_BUSINESS_RULES.md` (§8):
    1. Trim khoảng trắng đầu/cuối.
    2. Chuyển về chữ thường (`ToLowerInvariant()`).
    3. Kiểm tra cú pháp local part và domain `@gmail.com`.
    4. Loại bỏ phần `+tag` trong local part.
    5. Loại bỏ dấu chấm (`.`) trong local part.
- Ví dụ: `Quang.Dev+campaign@gmail.com` và `quangdev@gmail.com` cùng chuẩn hóa thành `quangdev@gmail.com`.
- Lưu `EmailOriginal` (đã trim) để phục vụ lịch sử/hiển thị admin; dùng `EmailNormalized` cho eligibility, `WinnerLock` và so sánh request idempotency.

### 3.2 Quy tắc Khóa trúng thưởng (WinnerLock)

- Phạm vi khóa: `(WheelId, EmailNormalized)`.
- Người chơi trúng thưởng ở bất kỳ phiên bản nào của Wheel (kể cả Version 1 hay Version 2) đều tạo 1 bản ghi `WinnerLock` (`IsActive = true`).
- Nếu tồn tại `WinnerLock` đang active cho `(WheelId, EmailNormalized)`: từ chối quay thưởng với mã lỗi HTTP 409 `EMAIL_ALREADY_WON`.
- Ô `NoPrize` ("Chúc bạn may mắn lần sau") **không** tạo `WinnerLock`, cho phép người chơi tiếp tục quay lại.
- Trúng giải không yêu cầu mã (`RequiresKey = false`) hoặc có mã (`RequiresKey = true`) đều kích hoạt `WinnerLock`.

### 3.3 Thuật toán chọn giải theo trọng số (Weighted Selection) & Thang đo 1,000,000

- **Thang đo chuẩn hóa:** Tổng trọng số `weight` của tất cả các ô trong một phiên bản bắt buộc phải bằng đúng **`1,000,000` (1 triệu = 100%)**.
- **Lý do kỹ thuật:**
    - Cho phép chia nhỏ độ chính xác đến **4 chữ số thập phân (`0.0001%`)** mà vẫn sử dụng kiểu số nguyên `int`, tránh hoàn toàn lỗi sai số làm tròn số thực (floating-point inaccuracies).
    - Hỗ trợ các giải đặc biệt/giải siêu hiếm (ví dụ tỷ lệ `0.5% = 5,000`, `0.01% = 100`, `0.0001% = 1`).
- **Công thức quy đổi:**
  $$\text{weight} = \text{Tỷ lệ \%} \times 10,000$$
    - `30%` $\rightarrow$ `weight = 300,000`
    - `70%` $\rightarrow$ `weight = 700,000`
    - `0.05%` $\rightarrow$ `weight = 500`
- **Khuyến nghị tích hợp Frontend/Admin UI:**
    - Trên màn hình quản trị người dùng, ô nhập liệu hiển thị dạng phần trăm `[ 30 ] %`.
    - Frontend tự động nhân `10,000` trước khi gửi request JSON xuống Backend (`weight = input * 10000`).
- **Thuật toán bốc thăm & xử lý hết key (`LUCKY_WHEEL_BUSINESS_RULES.md` — Invariant 19, §9.4–9.7, Scenario J):**
    - Trước khi bốc thăm, xác minh cấu hình phiên bản Active: danh sách phân khúc khác rỗng, mọi trọng số đều dương, tổng trọng số đúng bằng `1,000,000`, có đúng một phân khúc `NoPrize` (`PrizeId = null`), mọi phân khúc phần thưởng đều có `PrizeId` trỏ tới `Prize` tồn tại, đang `IsEnabled = true` và thuộc đúng `Wheel`. Nếu cấu hình lỗi, ném lỗi thật (`InvalidOperationException` -> HTTP 500 `INTERNAL_SERVER_ERROR` không lộ chi tiết nội bộ); tuyệt đối không tự sửa cấu hình, không loại segment để chuẩn hóa lại xác suất và không coi là hết key.
    - Sử dụng tổng lũy tích `long` trên toàn bộ phân khúc của phiên bản Active và khởi tạo ngẫu nhiên từ CSPRNG `GetInt64(0, 1_000_000)`.
    - **Giữ nguyên xác suất cấu hình khi hết key:** Không truy vấn đếm key `Available` để lọc ứng viên trước khi quay. Nếu giải `A` hết key, khoảng chọn của `A` vẫn được giữ nguyên trên thang `1,000,000`, các giải khác (ví dụ `B`) giữ nguyên khoảng chọn và xác suất cấu hình, không phân phối lại trọng số và không bốc lại.
    - **Fallback sang `NoPrize` khi không phân bổ được key:** Khi giải `RequiresKey = true` được chọn nhưng truy vấn phân bổ nguyên tử không trả về key `Available`, hệ thống chuyển kết quả hiệu lực sang nhánh lưu/trả `NoPrize` dùng chung với phân khúc `NoPrize` của cùng phiên bản:
        - Database: `Spin.Result = NoPrize`, `Spin.Status = Completed`, `Spin.PrizeId = null`, `Spin.PrizeKeyId = null`, `Spin.WheelVersionPrizeId = NoPrizeSegment.Id`, không tạo `WinnerLock`, không gán hay thay đổi `PrizeKey`, lưu đúng một `SpinHistory` rồi commit.
        - Response HTTP 200: `Result = "NoPrize"`, `Prize = null`, `EntryId = NoPrizeSegment.Id`, `DisplayOrder = NoPrizeSegment.DisplayOrder`.
        - Log chẩn đoán an toàn chỉ ghi các ID (`WheelId`, `WheelVersionId`, `SelectedSegmentId`, `PrizeId`, `FallbackSegmentId`) và lý do `NO_AVAILABLE_PRIZE_KEY`; không ghi email, key, receipt token hay idempotency key.
    - **Phân biệt hết key với lỗi thật:** Chỉ trường hợp không lấy được key `Available` mới fallback `NoPrize`. Lỗi SQL, timeout, lỗi giải mã key (`CryptographicException`) hoặc lỗi cấu hình đều lan truyền như lỗi thật và rollback transaction.
    - **Giới hạn hiện tại (`RequiresKey = false`):** Hệ thống chưa áp dụng kiểm soát tồn kho theo `TotalQuantity` cho các giải `RequiresKey = false`; hành vi này được giữ nguyên trong bước hiện tại.

### 3.4 Concurrency, Idempotency đồng thời & Tính nguyên tử của Spin

- **Lớp bảo vệ unique constraint của SQL Server (giữ nguyên schema hiện tại):**
    - `IX_SpinHistories_IdempotencyKey` (`UNIQUE` trên `SpinHistories.IdempotencyKey`): đảm bảo tối đa 1 Spin cho mỗi `IdempotencyKey`.
    - `IX_WinnerLocks_WheelId_EmailNormalized` (`UNIQUE` filtered `WHERE [IsActive] = 1`): đảm bảo tối đa 1 `WinnerLock` active cho mỗi `(WheelId, EmailNormalized)` trên một Wheel.
    - `IX_SpinHistories_PrizeKeyId` (`UNIQUE` filtered `WHERE [PrizeKeyId] IS NOT NULL`) + `PrizeKeys.RowVersion`: đảm bảo một `PrizeKey` chỉ gán cho tối đa 1 Spin.
    - `IX_SpinHistories_ReceiptToken` (`UNIQUE` trên `SpinHistories.ReceiptToken`).
    - `IX_WheelVersions_WheelId` (`UNIQUE` filtered `WHERE [Status] = 'Active'`) + `WheelVersions.RowVersion`: đảm bảo tối đa 1 version `Active` cho mỗi Wheel.
- **Điểm admission, phân tách mốc thời gian & phối hợp khóa giữa `SpinAsync` và Admin lifecycle:**
    - Không dùng lock/semaphore trong bộ nhớ; mọi tranh chấp được phân xử qua transaction và row-level locks của SQL Server theo thứ tự khóa nhất quán `Wheels -> WheelVersions -> PrizeKeys -> SpinHistories -> WinnerLocks` nhằm **giảm nguy cơ** xung đột vòng khóa/deadlock (không cam kết loại trừ tuyệt đối deadlock dưới mọi kế hoạch thực thi của SQL Server; nếu SQL Server vẫn trả deadlock `1205` hoặc lỗi tạm thời, transaction sẽ rollback/dọn state cục bộ và được `SqlServerRetryingExecutionStrategy` retry an toàn kèm `TryResolveIdempotencyAsync`).
    - Trong `SpinAsync`, hệ thống lấy khóa shared mức dòng trên `Wheels` (`WITH (REPEATABLEREAD, ROWLOCK)`) và bản ghi `WheelVersions` có `Status = 'Active'` (`WITH (REPEATABLEREAD, ROWLOCK)`). **Điểm admission** nằm ngay sau khi cả hai khóa `Wheel` và `WheelVersion` đã được giữ: hệ thống đọc mốc thời gian UTC mới (`admittedAtUtc = clock.UtcNow.UtcDateTime`) để kiểm tra `StartAtUtc <= admittedAtUtc <= EndAtUtc`. Nếu request đã chờ khóa qua quá hạn lịch hoạt động, transaction được rollback/dispose và ưu tiên gọi `TryResolveIdempotencyAsync` trước khi trả `WHEEL_NOT_AVAILABLE`.
    - Các mốc thời gian được tách bạch rõ ràng: `AcceptedTermsAtUtc` ghi nhận tại thời điểm request vượt qua bước validate điều khoản (không bị dịch chuyển tùy tiện do chờ khóa hoặc retry); `admittedAtUtc` dùng cho kiểm tra lịch hoạt động tại điểm admission; `AssignedAtUtc` và `ExpiresAtUtc` (`assignedAtUtc + ClaimDurationMinutes`) được tính theo thời điểm gán key sau khi cấp phát `PrizeKey` thành công để không làm rút ngắn thời hạn nhận thưởng do chờ khóa trước đó.
    - Các lượt quay công khai đồng thời giữ khóa `S` tương thích lẫn nhau nên không chặn nhau, đồng thời tuần tự hóa an toàn với việc Admin khóa/disable `Wheel` (`X` lock trên `Wheels`) và `CloseVersionAsync`/`ActivateVersionAsync` (khóa `WITH (XLOCK, ROWLOCK)` trên `Wheels` ngay đầu transaction admin).
    - Câu truy vấn phân bổ mã sử dụng locking hint mức dòng của SQL Server:
      ```sql
      SELECT TOP (1) * FROM [PrizeKeys] WITH (UPDLOCK, READPAST, ROWLOCK)
      WHERE [PrizeId] = @prizeId AND [Status] = 'Available'
      ORDER BY [CreatedAtUtc] ASC
      ```
        - `UPDLOCK`: Đặt update lock ngay khi đọc, giảm nguy cơ deadlock khi nhiều luồng cùng đọc rồi nâng cấp khóa để cập nhật.
        - `READPAST`: Bỏ qua dòng đang bị transaction khác khóa giữ, nên khi 2 request cùng tranh key cuối, một request giữ khóa và request còn lại nhận `null` để fallback `NoPrize` ngay theo quy tắc Bước 2.
        - `ROWLOCK`: Khóa ở cấp bản ghi thay vì page/table.
- **Xử lý rollback, cô lập `ChangeTracker` và retry của `ExecutionStrategy`:**
    - Sử dụng `SaveChangesAsync(acceptAllChangesOnSuccess: false)` + `CommitAsync` + `ChangeTracker.AcceptAllChanges()` để giữ nguyên `OriginalValues` của `PrizeKey` nếu `CommitAsync` gặp lỗi.
    - Mọi truy vấn đọc trạng thái đều dùng `AsNoTracking()`; khi thất bại hoặc va chạm, `CleanupAttemptState` chỉ khôi phục `OriginalValues` của `allocatedKey` (nếu `Modified`) và detach 3 entity cục bộ của attempt (`allocatedKey`, `pendingSpin`, `pendingWinnerLock`) mà **không** gọi `ChangeTracker.Clear()`, giữ nguyên các entity ngoài operation nếu có.
    - Chỉ phân loại `DbUpdateException` khi inner `SqlException` có mã `2601` hoặc `2627` khớp đúng tên index (`IX_SpinHistories_IdempotencyKey`, `IX_WinnerLocks_WheelId_EmailNormalized`, `IX_SpinHistories_PrizeKeyId`, `IX_SpinHistories_ReceiptToken`); mọi lỗi SQL/EF khác (`DbUpdateConcurrencyException`, check/FK error, timeout, transient error) đều rollback, dọn state và lan truyền đúng nguyên bản để `ExecutionStrategy` retry hoặc trả lỗi 5xx.
    - Sau khi rollback & đóng transaction lỗi hoàn toàn, hệ thống luôn đọc lại `TryResolveIdempotencyAsync` bằng trạng thái sạch trước khi kết luận `WHEEL_NOT_AVAILABLE` hoặc `EMAIL_ALREADY_WON` — đảm bảo replay luôn được ưu tiên ngay cả khi `WinnerLock` hoặc việc đóng Wheel diễn ra trước khi request thứ hai chạm tới `IX_SpinHistories_IdempotencyKey`.
    - Khi rớt kết nối ngay sau commit (`commit-confirmation failure`), lần retry tiếp theo của `SqlServerRetryingExecutionStrategy` chạy `TryResolveIdempotencyAsync` ở đầu attempt mới và replay đúng kết quả đã commit mà không tạo bản ghi trùng.

### 3.5 Snapshot thông tin giải, dữ liệu cũ & hướng dẫn nhận thưởng

- **Lưu snapshot tại `SpinHistory`:**
    - `SpinHistory` bổ sung hai cột nullable `PrizeNameSnapshot` (`nvarchar(200)`) và `PrizeDescriptionSnapshot` (`nvarchar(1000)`).
    - Mọi lượt `Win` mới tạo qua `SpinHistory.CreateWin(...)` bắt buộc cung cấp `PrizeNameSnapshot` không rỗng (`<= 200` ký tự) và `PrizeDescriptionSnapshot` (`null` hoặc `<= 1000` ký tự), được ghi nhận từ `Prize` tại thời điểm quay trong cùng transaction tạo Spin / gán `PrizeKey` / tạo `WinnerLock`.
    - Mọi lượt `NoPrize` (bao gồm cả fallback hết key) luôn lưu `PrizeNameSnapshot = null` và `PrizeDescriptionSnapshot = null`.
    - Không lưu plaintext key hoặc bản sao ciphertext key trong snapshot; replay và hiển thị kết quả tiếp tục đọc/giải mã từ `PrizeKey` gốc liên kết qua `SpinHistory.PrizeKeyId` và không tự chuyển sang replacement key của Giai đoạn 9.
- **Ổn định lịch sử & xử lý bản ghi `Win` cũ trước migration:**
    - Khi admin đổi tên hoặc mô tả `Prize`, các lượt quay cũ vẫn trả nguyên tên/mô tả đã lưu trong snapshot ở cả replay (`PublicWheelService.BuildReplayResponseAsync`) và lịch sử admin (`PrizeKeyService.GetWheelSpinsAsync`); lượt quay mới sử dụng thông tin `Prize` hiện hành.
    - Trường hợp `Prize.Description` lúc quay là `null` vẫn giữ nguyên `null` khi replay dù admin bổ sung mô tả sau đó.
    - Với bản ghi `Win` cũ đã tồn tại trước migration `AddSpinHistoryPrizeSnapshot` (không có snapshot, `PrizeNameSnapshot == null`): **không** backfill từ bảng `Prizes` hiện tại và **không** đọc fallback âm thầm từ `Prize` hiện hành (tránh gán nhầm thông tin giải đã đổi sau này). Cả replay và lịch sử admin đều dùng fallback trung tính cố định: `Name = "Phần thưởng"` (`SpinHistory.LegacyMissingPrizeNameFallback`) và `Description = null`, đồng thời giữ nguyên `PrizeId`, `PrizeKey` gốc và `KeyExpiresAtUtc`.
- **Hướng dẫn nhận thưởng (Claim instructions):**
    - Kiểm tra domain model hiện tại (`Wheel`, `WheelVersion`, `Prize`) cho thấy **chưa tồn tại trường nguồn riêng cho hướng dẫn nhận thưởng**.
    - Public API cố ý không tự bịa nội dung hướng dẫn và không tái sử dụng `Wheel.Terms` hay `Prize.Description` làm hướng dẫn nhận thưởng. Khi domain/admin bổ sung trường dữ liệu chính thức, có thể mở rộng contract và snapshot tương ứng.

---

## 4. Đặc tả API

### 4.1 Lấy cấu hình vòng quay công khai

- **Endpoint:** `GET /api/public/wheels/{slug}`
- **Authentication:** Anonymous (`[AllowAnonymous]`)
- **Response 200 OK:**
    ```json
    {
        "wheelId": "550e8400-e29b-41d4-a716-446655440000",
        "name": "Vòng quay hè rực rỡ",
        "slug": "vong-quay-he-2026",
        "terms": "Mỗi Gmail hợp lệ chỉ được trúng tối đa 1 giải trên vòng quay này.",
        "versionId": "660e8400-e29b-41d4-a716-446655440001",
        "versionNumber": 1,
        "startAtUtc": "2026-08-01T00:00:00Z",
        "endAtUtc": "2026-08-31T23:59:59Z",
        "entries": [
            {
                "id": "770e8400-e29b-41d4-a716-446655440002",
                "displayOrder": 1,
                "label": "Voucher 500k",
                "color": "#FF5733",
                "imageUrl": "https://cdn.luckywheel.com/voucher500.png",
                "isNoPrize": false
            },
            {
                "id": "880e8400-e29b-41d4-a716-446655440003",
                "displayOrder": 2,
                "label": "Chúc bạn may mắn lần sau",
                "color": "#E0E0E0",
                "imageUrl": null,
                "isNoPrize": true
            }
        ]
    }
    ```
- **Response 404 Not Found:** Mã lỗi `WHEEL_NOT_AVAILABLE` khi vòng quay không tồn tại, bị disable hoặc không có phiên bản Active hợp lệ trong cửa sổ thời gian hiệu lực.

---

### 4.2 Thực hiện quay thưởng

- **Endpoint:** `POST /api/public/wheels/{slug}/spins`
- **Authentication:** Anonymous (`[AllowAnonymous]`)
- **Headers:**
    - `Idempotency-Key`: Chuỗi an toàn (độ dài 1..100 ký tự alphanumeric, `-`, `_`, `.`).
- **Request Body:**
    ```json
    {
        "email": "nguyenvanquang@gmail.com",
        "acceptedTerms": true
    }
    ```
- **Response 200 OK (Trúng thưởng):**
    ```json
    {
        "spinId": "110e8400-e29b-41d4-a716-446655440010",
        "receiptToken": "RC-8F2D3A1B9C4E0F7D1234567890ABCDEF",
        "result": "Win",
        "entryId": "770e8400-e29b-41d4-a716-446655440002",
        "displayOrder": 1,
        "message": "Chúc mừng bạn đã trúng thưởng",
        "prize": {
            "id": "990e8400-e29b-41d4-a716-446655440004",
            "name": "Voucher 500k",
            "description": "Giảm 500k cho đơn hàng từ 2 triệu",
            "key": "LW-ABCD-EFGH-1234-5678",
            "keyExpiresAtUtc": "2026-08-19T13:00:00Z"
        },
        "completedAtUtc": "2026-08-19T12:00:00Z"
    }
    ```
- **Response 200 OK (Chúc may mắn lần sau):**
    ```json
    {
        "spinId": "220e8400-e29b-41d4-a716-446655440020",
        "receiptToken": "RC-A1B2C3D4E5F60718293A4B5C6D7E8F90",
        "result": "NoPrize",
        "entryId": "880e8400-e29b-41d4-a716-446655440003",
        "displayOrder": 2,
        "message": "Chúc bạn may mắn lần sau",
        "prize": null,
        "completedAtUtc": "2026-08-19T12:05:00Z"
    }
    ```
- **Ghi chú contract:**
    - `completedAtUtc` giữ nguyên tên trường hiện hành và ánh xạ trực tiếp từ `SpinHistory.CreatedAtUtc` (thời điểm UTC được ghi nhận khi khởi tạo bản ghi `SpinHistory` trước khi commit transaction, không phải thời điểm SQL Server xác nhận commit).
    - `prize.name` và `prize.description` được lấy từ snapshot lưu trên `SpinHistory` (hoặc fallback trung tính `"Phần thưởng"` / `null` với bản ghi `Win` cũ trước migration thiếu snapshot).
    - Chưa bổ sung trường hướng dẫn nhận thưởng do domain model chưa có trường nguồn riêng cho dữ liệu này.
- **Bảng mã lỗi (Error Codes):**
  | HTTP Status | Error Code | Mô tả |
  | :--- | :--- | :--- |
  | `400 Bad Request` | `VALIDATION_ERROR` | Thiếu Idempotency-Key, email không phải Gmail, chưa đồng ý điều khoản |
  | `404 Not Found` | `WHEEL_NOT_AVAILABLE` | Vòng quay không tồn tại, bị disable hoặc không có phiên bản active |
  | `409 Conflict` | `EMAIL_ALREADY_WON` | Gmail này đã trúng thưởng trên vòng quay |
  | `409 Conflict` | `IDEMPOTENCY_KEY_REUSED` | Tái sử dụng Idempotency-Key với email hoặc wheel khác |
  | `429 Too Many Requests` | `TOO_MANY_REQUESTS` | Vượt quá tần suất quay tối đa cho phép (Rate Limit) |
  | `500 Internal Server Error` | `INTERNAL_SERVER_ERROR` | Lỗi cấu hình phiên bản Active, lỗi SQL/timeout hoặc lỗi giải mã (không lộ chi tiết nội bộ) |

---

## 5. Danh sách Migration & Thay đổi Schema

- **Migration:** `20260819071619_AddPublicSpinSupport.cs`
- **Thay đổi chi tiết:**
    1. `SpinHistories`:
        - Cột `IdempotencyKey`: Chuyển kiểu từ `uniqueidentifier` sang `nvarchar(100)` có unique index.
        - Cột `WheelVersionPrizeId`: Thêm khóa ngoại trỏ tới `WheelVersionPrizes(Id)` với hành vi `DeleteBehavior.Restrict`.
        - Cột `AcceptedTermsAtUtc`: Thêm cột `datetime2` lưu thời điểm người dùng chấp thuận điều khoản.
        - Index mới: `IX_SpinHistories_WheelVersionPrizeId_CreatedAtUtc`.
    2. `WinnerLocks`:
        - Cột `PrizeKeyId`: Cho phép nhận giá trị `NULL` (hỗ trợ trường hợp trúng giải vật lý / giải không yêu cầu mã).

- **Migration:** `20261119171918_AddSpinHistoryPrizeSnapshot.cs`
- **Thay đổi chi tiết:**
    1. `SpinHistories`:
        - Cột `PrizeNameSnapshot`: Thêm cột nullable `nvarchar(200) NULL` (không giá trị mặc định, không backfill dữ liệu cũ).
        - Cột `PrizeDescriptionSnapshot`: Thêm cột nullable `nvarchar(1000) NULL` (không giá trị mặc định, không backfill dữ liệu cũ).
        - Tương thích nâng cấp trực tiếp trên database đã có bản ghi `SpinHistories`.


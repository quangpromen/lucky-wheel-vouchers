# Giai đoạn 5: Admin Authentication & Authorization (JWT)

## Phạm vi

Đã triển khai login riêng cho admin, access token JWT, policy `AdminOnly`, kiểm tra tài khoản active trên mỗi request, Swagger Bearer và bootstrap admin chỉ ở Development. Không triển khai registration, refresh token, CRUD, spin/redeem, sinh key hay worker.

## Luồng và API

`POST /api/admin/auth/login` nhận `{ "username": "admin@example.com", "password": "<password>" }`. `username` được trim/lowercase và đối chiếu với `AdminUser.Email`. Thành công trả access token Bearer, expiration và thông tin admin an toàn. Sai email, password hoặc inactive đều trả 401 `INVALID_CREDENTIALS` giống nhau.

`GET /api/admin/auth/me` yêu cầu Bearer token và policy `AdminOnly`; trả `id`, `username`, `displayName`. Thiếu/sai/hết hạn token hoặc admin đã bị vô hiệu hóa trả 401 `UNAUTHORIZED`. `/health` và `/api/system/info` vẫn public.

JWT dùng HMAC-SHA256, validate issuer, audience, signing key, lifetime và `ClockSkew = 0`. Claims gồm `sub`, `unique_name`, `name`, `role=Admin`, `jti`; lifetime mặc định 30 phút. Mỗi lần validate token, server đọc admin ID và query DB không cache để bảo đảm user vẫn tồn tại và active.

## Secret và bootstrap Development

Không có signing key/password thật trong source. Cấu hình bằng User Secrets hoặc biến môi trường (`Jwt__SigningKey`, `BootstrapAdmin__Username`, `BootstrapAdmin__Password`):

```bash
dotnet user-secrets set "Jwt:SigningKey" "<random-secret-at-least-32-bytes>" --project src/LuckyWheel.Api
dotnet user-secrets set "BootstrapAdmin:Username" "<your-admin-email>" --project src/LuckyWheel.Api
dotnet user-secrets set "BootstrapAdmin:Password" "<strong-password>" --project src/LuckyWheel.Api
dotnet user-secrets set "BootstrapAdmin:DisplayName" "<display-name>" --project src/LuckyWheel.Api
```

Seeder chỉ chạy Development, bỏ qua khi thiếu config, idempotent theo email, không reset password và không log credential. Seeder không tự chạy migration.

## Database, package và kiểm tra

- Migration: `AddAdminAuthentication`, chỉ thêm `PasswordHash` và nullable `LastLoginAtUtc` vào `AdminUsers`; không database update.
- Package mới: `Microsoft.AspNetCore.Authentication.JwtBearer` 8.0.11, publisher Microsoft, dùng cho JWT Bearer .NET 8.
- `dotnet restore`: PASS.
- `dotnet build --no-restore`: PASS, 0 warning/error.
- Unit tests: PASS 76/76, gồm password verify và JWT claims/expiration.
- `dotnet test --no-restore`: PARTIAL — 76/76 unit PASS; 17/35 integration PASS. Các integration/API không phụ thuộc SQL Server PASS, gồm validation login, credential response chung, token sai/hết hạn, token active, admin inactive sau khi phát token, `/health`, `/api/system/info` và error handling. 18 persistence tests bị block ngay tại SQL Server fixture vì host hiện tại không hỗ trợ encryption.
- Chỉ thao tác file trong repository LuckyWheel; không sửa project/repository khác.

## Security & Git Readiness Review

- Ngày review: 2026-08-13 (UTC).
- Phạm vi: file Git tracked, staged, unstaged và untracked trong repository; cấu hình JWT, bootstrap admin, package, `.gitignore`, documentation và lịch sử Git hiện có.
- Đã sửa: thay signing key cố định của test bằng key ngẫu nhiên runtime; thêm `.gitignore` cho `.env`, local appsettings, certificate/private-key, database và backup; loại marker test có dạng password assignment; tắt EventLog chỉ trong test host để test không phụ thuộc quyền Windows.
- Secret trong file hiện tại: không phát hiện password, signing key, token, API key hay connection string có password thật. Placeholder được giữ nguyên.
- Git history: không phát hiện credential thật. Scanner heuristic đánh dấu hai fixture test cũ do marker giả lập lỗi có dạng password; chúng không phải secret và marker hiện tại đã được thay bằng giá trị không nhạy cảm. Không rewrite lịch sử Git.
- Package: chỉ có `Microsoft.AspNetCore.Authentication.JwtBearer` 8.0.11 (Microsoft), tương thích .NET 8.
- Kết quả thật: `dotnet restore` PASS; `dotnet build --no-restore` PASS (0 warning/error); `dotnet test --no-restore` PARTIAL (76 unit PASS, 17/35 integration PASS). Persistence integration bị block vì SQL Server local không hỗ trợ encryption; không thay đổi database hay hệ điều hành để ép chạy.

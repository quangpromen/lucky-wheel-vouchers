# Giai đoạn 6: Admin Wheel, Wheel Version và Prize Management

## Phạm vi đã triển khai

- Wheel: tạo, xem chi tiết, danh sách phân trang, cập nhật; không có hard-delete.
- Prize catalog theo Wheel: tạo, xem, danh sách phân trang/filter `wheelId` và `requiresKey`, cập nhật có optimistic concurrency.
- Wheel Version: tạo Draft với số version tuần tự trong transaction `Serializable`, xem, danh sách theo Wheel và cập nhật schedule Draft.
- Cấu hình segment `WheelVersionPrize`: thêm, sửa, xóa, reorder trên Draft; hỗ trợ prize/no-prize; weight bắt buộc lớn hơn 0.
- Tất cả controller Giai đoạn 6 yêu cầu policy `AdminOnly` ở cấp controller.
- Không có endpoint activate/close/version delete, Wheel/Prize delete, key generation, spin, redeem/cancel/expire hoặc worker.

## API

| Method | Route | Chức năng |
| --- | --- | --- |
| POST | `/api/admin/wheels` | Tạo Wheel |
| GET | `/api/admin/wheels/{id}` | Chi tiết Wheel |
| GET | `/api/admin/wheels?page=1&pageSize=20` | Danh sách Wheel |
| PUT | `/api/admin/wheels/{id}` | Cập nhật Wheel |
| POST | `/api/admin/prizes` | Tạo Prize |
| GET | `/api/admin/prizes/{id}` | Chi tiết Prize |
| GET | `/api/admin/prizes` | Danh sách/filter Prize |
| PUT | `/api/admin/prizes/{id}` | Cập nhật Prize |
| POST | `/api/admin/wheels/{wheelId}/versions` | Tạo Draft Version |
| GET | `/api/admin/wheels/{wheelId}/versions` | Danh sách Version |
| GET | `/api/admin/wheel-versions/{id}` | Chi tiết Version và segments |
| PUT | `/api/admin/wheel-versions/{id}` | Cập nhật Draft Version |
| POST | `/api/admin/wheel-versions/{id}/prizes` | Thêm segment |
| PUT | `/api/admin/wheel-versions/{id}/prizes/{segmentId}` | Cập nhật segment |
| DELETE | `/api/admin/wheel-versions/{id}/prizes/{segmentId}?rowVersion=...` | Xóa segment |
| PUT | `/api/admin/wheel-versions/{id}/prizes/reorder` | Sắp xếp toàn bộ segments |

`rowVersion` trả về dưới dạng base64 và bắt buộc với mutable resource đã mapping concurrency token. Token cũ trả HTTP 409 `CONFLICT`.

## Toàn vẹn dữ liệu và concurrency

- Slug Wheel được kiểm tra trước và bảo vệ bằng unique index database.
- `(WheelId, VersionNumber)` và `(WheelVersionId, DisplayOrder)` tiếp tục được bảo vệ bằng unique index.
- Version number dùng transaction `Serializable` kết hợp unique constraint và conflict handling, không dùng `MAX + 1` ngoài transaction.
- Prize đã được Version/Spin tham chiếu không được đổi `RequiresKey` hoặc giảm quantity.
- Segment chỉ nhận Prize đang enabled thuộc cùng Wheel với Version.
- Active/Closed Version bị chặn mọi thay đổi cấu hình.
- Migration `AddAdminWheelManagementConcurrency` thêm rowversion cho `WheelVersionPrizes` và đổi check constraint weight từ `>= 0` thành `> 0`.
- Các write quan trọng tạo `AuditLog` an toàn, không chứa secret/key.

## Package

Không thêm package mới.

## Xác minh

- `dotnet build --no-restore`: PASS, 0 warning, 0 error.
- Unit tests: PASS 80/80.
- Integration không cần SQL Server: PASS 26/26, gồm kiểm tra metadata `AdminOnly`, HTTP 401 khi thiếu token và RowVersion của `WheelVersionPrize`.
- 18 persistence integration tests bị block bởi SQL Server local yêu cầu encryption mà môi trường hiện tại không hỗ trợ, giống Giai đoạn 5. Không update/drop database để ép chạy.

## Trạng thái

`COMPLETED` về code/build và các test chạy được; database migration đã tạo nhưng chưa áp dụng lên database.

## Security & Git Readiness Review

- Ngày review: 2026-08-13.
- Phạm vi: toàn bộ tài liệu `src/doc`, source/diff staged/unstaged/untracked, Git tracked files, `.gitignore`, project dependencies, API authorization metadata, migration và lịch sử Git hiện có.
- Lỗi đã sửa:
  - Bổ sung test HTTP xác nhận endpoint Giai đoạn 6 trả 401 khi thiếu token.
  - Bổ sung `WheelVersionPrize` vào kiểm tra EF RowVersion metadata.
  - Thay connection-string example trong tài liệu Giai đoạn 3 bằng placeholder rõ ràng; không ghi lại giá trị cũ.
- Secret scan file hiện tại: không phát hiện password, JWT signing key, token, API key, Prize Key, connection string có password thật, certificate/private key hoặc secret export trong tracked/staged/unstaged/untracked files.
- Git history: phát hiện một connection-string example cũ có password-like value trong lịch sử của `src/doc/03-DATABASE-EF-CORE.md`. Không rewrite history. Phải xác nhận đây là fixture giả; nếu từng được dùng thật thì rotate credential trước khi push/deploy.
- `.gitignore`: có đủ `.env`, local appsettings, `secrets.json`, certificate/private key, database/backup, `bin/`, `obj/` và `.vs/`. Không có build output/log/database/secret file đang được Git track.
- Dependency: Giai đoạn 6 không thêm package; không phát hiện package mới đáng ngờ hoặc prerelease.
- Kết quả thực tế:
  - `dotnet restore`: PASS sau khi cho phép truy cập NuGet.org; lần chạy sandbox đầu bị chặn network.
  - `dotnet build --no-restore`: PASS, 0 warning/error.
  - Unit: PASS 80/80.
  - Integration khả dụng: PASS 26/26.
  - Full `dotnet test --no-restore`: 80 unit PASS; integration 26 PASS và 18 persistence tests bị block tại fixture vì SQL Server local yêu cầu encryption nhưng máy review không hỗ trợ. Đây là giới hạn môi trường, chưa phải test assertion/code failure.
- Chỉ thao tác bên trong repository LuckyWheel; không commit/push, đổi branch, rewrite history, update/drop database hoặc triển khai Giai đoạn 7+.
- Git readiness: `READY WITH NOTES` nếu password-like value trong history được xác nhận là giả; nếu không thể xác nhận hoặc từng dùng thật thì `NOT READY` cho tới khi credential được rotate và lịch sử được xử lý theo quy trình được phê duyệt.

# Lucky Wheel Development Documentation

## Hướng dẫn cho AI và developer

Trước khi tiếp tục phát triển, bắt buộc đọc theo thứ tự:

1. `LUCKY_WHEEL_BUSINESS_RULES.md`
2. Tài liệu các giai đoạn đã hoàn thành theo số thứ tự.
3. Source code thực tế liên quan đến giai đoạn tiếp theo.

Business Rules là nguồn nghiệp vụ chính thức. Tài liệu từng giai đoạn mô tả những gì đã được triển khai trong source code.

## Trạng thái dự án

| Giai đoạn | Nội dung | Tài liệu | Trạng thái |
| --- | --- | --- | --- |
| 0 | Business Rules | `LUCKY_WHEEL_BUSINESS_RULES.md` | COMPLETED |
| 1 | Project Initialization | `01-PROJECT-INITIALIZATION.md` | COMPLETED |
| 2 | Domain Layer | `02-DOMAIN-LAYER.md` | COMPLETED |
| 3 | Database & EF Core | `03-DATABASE-EF-CORE.md` | COMPLETED |
| 4 | Shared Components, Validation & Error Handling | `04-SHARED-COMPONENTS-VALIDATION-ERROR-HANDLING.md` | COMPLETED |
| 5 | Authentication & Authorization | `05-ADMIN-AUTHENTICATION-JWT.md` | COMPLETED |
| 6 | Admin Wheel/Version/Prize Management | `06-ADMIN-WHEEL-VERSION-PRIZE-MANAGEMENT.md` | COMPLETED |
| 7 | Prize Key Management & Wheel Version Activation | `07-PRIZE-KEY-MANAGEMENT-AND-VERSION-ACTIVATION.md` | COMPLETED |

## Giai đoạn hiện tại

Giai đoạn vừa hoàn thành: **Giai đoạn 7 — Prize Key Management & Wheel Version Activation.**

Kết quả kiểm tra Giai đoạn 7:
- `dotnet restore`: PASS
- `dotnet build --no-restore`: PASS (0 error, 0 warning)
- `dotnet test --no-restore`: BLOCKED một phần bởi SQL Server local; Unit 114/114 và 43 integration khả dụng PASS, 18 persistence tests bị block bởi môi trường.

Security & Git Readiness Review ngày 2026-08-16: Giai đoạn 7 `READY WITH NOTES`. Crypto/storage, activation transaction và migration đã được harden; build 0 warning/error và mọi test khả dụng pass. Cần chạy lại 18 SQL Server tests trong môi trường hỗ trợ encryption, xác nhận password-like example cũ trong Git history là fixture, và dùng quy trình key-aware nếu database đã chứa PrizeKey theo schema cũ. Chi tiết tại `07-PRIZE-KEY-MANAGEMENT-AND-VERSION-ACTIVATION.md`.

## Giai đoạn tiếp theo

Giai đoạn 8 — Public Spin, Winner Lock & Spin Execution.

## Quy tắc cập nhật tài liệu

Sau mỗi giai đoạn, AI/developer phải:

- Kiểm tra source code thực tế.
- Chạy build và test.
- Tạo hoặc cập nhật tài liệu tương ứng.
- Chỉ đánh dấu `COMPLETED` khi code và test đạt yêu cầu.

## Security & Git Readiness Review — Giai đoạn 5

- Review ngày 2026-08-13 (UTC), bao gồm thay đổi tracked/staged/unstaged/untracked, cấu hình JWT, `.gitignore`, package và documentation.
- Không phát hiện secret thật trong file hiện tại; không có build artifact, database, certificate/private-key hay secret file đang được Git track.
- Lịch sử Git được quét theo heuristic; chỉ có marker fixture test cũ bị nhận diện nhầm, không phải credential. Không rewrite Git history.
- `dotnet restore` và build pass; persistence integration bị block bởi SQL Server local không hỗ trợ encryption. Chi tiết và kết quả test đầy đủ ở `05-ADMIN-AUTHENTICATION-JWT.md`.

## Security & Git Readiness Review — Giai đoạn 6

- Review ngày 2026-08-13: toàn bộ endpoint Giai đoạn 6 yêu cầu `AdminOnly`; test HTTP thiếu token trả 401 và test EF metadata RowVersion đều pass.
- `dotnet restore` PASS; build PASS (0 warning/error); unit PASS 80/80; integration khả dụng PASS 26/26. Full test còn 18 persistence tests bị block bởi SQL Server local không hỗ trợ encryption.
- Secret scan file hiện tại không phát hiện credential/token/key thật; tracked files không có build output, log, database, backup, local secret config hay certificate/private key.
- Đã thay một password-like connection-string example trong tài liệu Giai đoạn 3 bằng placeholder. Mẫu cũ vẫn tồn tại trong Git history; không rewrite history. Cần xác nhận đó là dữ liệu giả hoặc rotate credential trước khi push/deploy nếu từng được sử dụng.
- Không thêm package ở Giai đoạn 6. Chi tiết tại `06-ADMIN-WHEEL-VERSION-PRIZE-MANAGEMENT.md`.

## Verification / Review (Giai đoạn 4)

- **Những điểm đã kiểm tra**: Application không phụ thuộc package ngoài; các exception, validation và `GlobalExceptionHandler` mapping mã lỗi, status chuẩn xác theo yêu cầu. Header `X-Correlation-ID` hoạt động. `IClock` chuẩn xác. API healthcheck còn hoạt động. Không có package lạ.
- **Những lỗi đã sửa**: Không có lỗi. Tất cả các unit/integration test đều thiết kế đúng và pass. Codebase đã hoàn thiện.
- **Package mới**: Không thêm package mới nào (chỉ bao gồm `Microsoft.AspNetCore.Mvc.Testing` và `Microsoft.Extensions.Configuration.UserSecrets` đã quy định ở các bước test trước đó).
- **Kết quả thật của build/test**:
  - `dotnet build --no-restore`: PASS (0 Error, 0 Warning)
  - `dotnet test --no-restore`: PASS (Total: 106, Passed: 106)
- **Các test chưa thể chạy và lý do**: Đã chạy thành công 106 test.
- **Xác nhận không thao tác ngoài repository LuckyWheel**: Đã xác nhận chỉ thao tác và kiểm tra trong nội bộ dự án này.

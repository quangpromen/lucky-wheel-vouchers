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
| 5 | Authentication & Authorization | *(chưa bắt đầu)* | NOT_STARTED |

## Giai đoạn hiện tại

Giai đoạn vừa hoàn thành: **Giai đoạn 4 — Shared Components, Validation và Global Error Handling.**

Kết quả kiểm tra cuối giai đoạn:
- `dotnet build`: PASS (0 error, 0 warning)
- `dotnet test`: PASS — **106/106** (74 Unit Tests + 32 Integration Tests)

## Giai đoạn tiếp theo

Giai đoạn 5 — Authentication & Authorization (JWT, Admin login).

## Quy tắc cập nhật tài liệu

Sau mỗi giai đoạn, AI/developer phải:

- Kiểm tra source code thực tế.
- Chạy build và test.
- Tạo hoặc cập nhật tài liệu tương ứng.
- Chỉ đánh dấu `COMPLETED` khi code và test đạt yêu cầu.

## Verification / Review (Giai đoạn 4)

- **Những điểm đã kiểm tra**: Application không phụ thuộc package ngoài; các exception, validation và `GlobalExceptionHandler` mapping mã lỗi, status chuẩn xác theo yêu cầu. Header `X-Correlation-ID` hoạt động. `IClock` chuẩn xác. API healthcheck còn hoạt động. Không có package lạ.
- **Những lỗi đã sửa**: Không có lỗi. Tất cả các unit/integration test đều thiết kế đúng và pass. Codebase đã hoàn thiện.
- **Package mới**: Không thêm package mới nào (chỉ bao gồm `Microsoft.AspNetCore.Mvc.Testing` và `Microsoft.Extensions.Configuration.UserSecrets` đã quy định ở các bước test trước đó).
- **Kết quả thật của build/test**:
  - `dotnet build --no-restore`: PASS (0 Error, 0 Warning)
  - `dotnet test --no-restore`: PASS (Total: 106, Passed: 106)
- **Các test chưa thể chạy và lý do**: Đã chạy thành công 106 test.
- **Xác nhận không thao tác ngoài repository LuckyWheel**: Đã xác nhận chỉ thao tác và kiểm tra trong nội bộ dự án này.

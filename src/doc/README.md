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
| 3 | Database & EF Core | `03-DATABASE-EF-CORE.md` | NOT_STARTED |

## Giai đoạn hiện tại

Giai đoạn vừa hoàn thành: Giai đoạn 2 — Domain Layer.

## Giai đoạn tiếp theo

Giai đoạn 3 — Database và Entity Framework Core.

## Quy tắc cập nhật tài liệu

Sau mỗi giai đoạn, AI/developer phải:

- Kiểm tra source code thực tế.
- Chạy build và test.
- Tạo hoặc cập nhật tài liệu tương ứng.
- Chỉ đánh dấu `COMPLETED` khi code và test đạt yêu cầu.

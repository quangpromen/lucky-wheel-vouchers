# Giai đoạn 4: Shared Components, Validation và Global Error Handling

## 1. Mục tiêu và phạm vi

Xây dựng nền tảng dùng chung cho toàn bộ API:

| Mục tiêu | Kết quả |
|-----------|---------|
| Validation nhất quán | `IValidator<T>` + `ValidationResult` thuần C# |
| Lỗi HTTP chuẩn RFC 7807 | `AddProblemDetails()` + `GlobalExceptionHandler` |
| Error code ổn định cho FE | 7 code cố định, xem bảng §3 |
| Correlation / trace id | `CorrelationIdMiddleware` + header `X-Correlation-ID` |
| Không lộ exception ở Production | Kiểm tra `IHostEnvironment.IsDevelopment()` trong handler |
| Abstraction thời gian (testable) | `IClock` + `SystemClock` |
| Không phụ thuộc framework bên thứ ba | Chỉ dùng .NET 8 / ASP.NET Core built-in |

**Không triển khai trong giai đoạn này:**
- Authentication / JWT / login admin
- CRUD Wheel, Prize, Version
- Key generation, spin engine, redemption, cancellation
- Worker expire key

---

## 2. Các abstraction đã tạo và vị trí source

### 2.1 Application Layer (`src/LuckyWheel.Application/Common/`)

```
Common/
├── Exceptions/
│   ├── ValidationException.cs           ← field-level errors dict, ErrorCode = VALIDATION_ERROR
│   ├── NotFoundException.cs             ← HTTP 404, ErrorCode = NOT_FOUND
│   ├── ConflictException.cs             ← HTTP 409, ErrorCode = CONFLICT
│   ├── ForbiddenException.cs            ← HTTP 403, ErrorCode = FORBIDDEN
│   └── BusinessRuleViolationException.cs← HTTP 400, ErrorCode = BUSINESS_RULE_VIOLATION
├── Validation/
│   ├── IValidator.cs                    ← interface IValidator<in TRequest>
│   └── ValidationResult.cs             ← IsValid, Errors, factories, ThrowIfInvalid()
└── Time/
    └── IClock.cs                        ← interface IClock { DateTimeOffset UtcNow; }
```

> `DomainException` (từ Giai đoạn 2) được **tái sử dụng**, map thành HTTP 400 `BUSINESS_RULE_VIOLATION`. Không tạo exception trùng lặp.

### 2.2 Infrastructure Layer (`src/LuckyWheel.Infrastructure/`)

```
Time/
└── SystemClock.cs    ← public sealed class SystemClock : IClock
                         → DateTimeOffset.UtcNow
                         → đăng ký Singleton trong DependencyInjection.cs
```

### 2.3 API Layer (`src/LuckyWheel.Api/`)

```
Errors/
└── GlobalExceptionHandler.cs   ← implements IExceptionHandler
                                   → map exception → ProblemDetails + errorCode + traceId
Middleware/
└── CorrelationIdMiddleware.cs  ← X-Correlation-ID resolve/validate/set
InternalsVisibleTo.cs           ← [InternalsVisibleTo("LuckyWheel.UnitTests")] cho test
```

**Thay đổi `Program.cs`:**
- `AddProblemDetails()` + `AddExceptionHandler<GlobalExceptionHandler>()`
- `app.UseExceptionHandler()` — đặt **đầu tiên** trong pipeline
- `app.UseMiddleware<CorrelationIdMiddleware>()` — ngay sau
- `public partial class Program { }` — cho `WebApplicationFactory` trong integration test

---

## 3. Danh sách Error Code + HTTP Status

| Loại lỗi | HTTP Status | `errorCode` | Exception Type |
|-----------|:-----------:|-------------|----------------|
| Validation | 400 | `VALIDATION_ERROR` | `ValidationException` |
| Business rule (Application) | 400 | `BUSINESS_RULE_VIOLATION` | `BusinessRuleViolationException` |
| Business rule (Domain) | 400 | `BUSINESS_RULE_VIOLATION` | `DomainException` |
| Không tìm thấy | 404 | `NOT_FOUND` | `NotFoundException` |
| Xung đột / concurrency | 409 | `CONFLICT` | `ConflictException` hoặc `DbUpdateConcurrencyException` |
| Không được phép | 403 | `FORBIDDEN` | `ForbiddenException` |
| Lỗi database save | 500 | `INTERNAL_SERVER_ERROR` | `DbUpdateException` |
| Lỗi không xác định | 500 | `INTERNAL_SERVER_ERROR` | `Exception` (catch-all) |

> **`UNAUTHORIZED` (401)** chưa được triển khai ở giai đoạn này vì chưa có JWT/authentication. Sẽ được thêm cùng giai đoạn xác thực.

---

## 4. JSON mẫu ProblemDetails

### 4.1 Response lỗi chuẩn (404)
```json
{
  "type": "https://httpstatuses.com/404",
  "title": "Resource not found",
  "status": 404,
  "detail": "Wheel 'abc123' was not found.",
  "instance": "/api/wheels/abc123",
  "traceId": "00-827af54c36633a84f59dca18861fb7f4-f12624f1a102cc9a-00",
  "errorCode": "NOT_FOUND"
}
```

### 4.2 Validation error (400) — có thêm `errors`
```json
{
  "type": "https://httpstatuses.com/400",
  "title": "Validation failed",
  "status": 400,
  "detail": "One or more validation errors occurred.",
  "instance": "/api/wheels",
  "traceId": "00-abc123...",
  "errorCode": "VALIDATION_ERROR",
  "errors": {
    "email": [
      "Email is required.",
      "Email must be a valid Gmail address."
    ],
    "name": [
      "Name is required."
    ]
  }
}
```

### 4.3 Business rule violation (400)
```json
{
  "type": "https://httpstatuses.com/400",
  "title": "Business rule violation",
  "status": 400,
  "detail": "The wheel is not currently active.",
  "instance": "/api/spin",
  "traceId": "00-xyz...",
  "errorCode": "BUSINESS_RULE_VIOLATION",
  "ruleCode": "WHEEL_NOT_ACTIVE"
}
```

### 4.4 Server error (500) — không lộ chi tiết
```json
{
  "type": "https://httpstatuses.com/500",
  "title": "An unexpected error occurred",
  "status": 500,
  "detail": "An unexpected server error occurred. Please try again later.",
  "instance": "/api/spin",
  "traceId": "00-...",
  "errorCode": "INTERNAL_SERVER_ERROR"
}
```

---

## 5. Cách hoạt động của Correlation Id / Trace Id

### Luồng resolve (ưu tiên theo thứ tự)

```text
Request đến
  → CorrelationIdMiddleware
      1. Đọc header X-Correlation-ID từ client
         → Validate: độ dài ≤ 64, chỉ [a-zA-Z0-9\-_\.], regex có timeout
         → HỢP LỆ → dùng giá trị của client
         → KHÔNG HỢP LỆ hoặc KHÔNG CÓ → bước tiếp
      2. Activity.Current?.Id (W3C TraceParent / OpenTelemetry)
      3. HttpContext.TraceIdentifier (ASP.NET Core fallback)
  → Lưu vào HttpContext.Items["CorrelationId"]
  → Set response header X-Correlation-ID
  → (nếu exception) GlobalExceptionHandler RE-SET header X-Correlation-ID
      (vì ExceptionHandlerMiddleware gọi Response.Clear() trước khi xử lý)
```

### Bảo mật

- Giá trị client-supplied KHÔNG được dùng cho authorization hoặc business identity.
- Validation chặt: regex có `TimeSpan.FromMilliseconds(100)` timeout chống ReDoS.
- Độ dài tối đa 64 ký tự.
- Ký tự cho phép: `[a-zA-Z0-9\-_.]` — không cho phép space, angle bracket, semicolon, newline, null byte.

### Lý do GlobalExceptionHandler phải tự set lại header

`ExceptionHandlerMiddleware` (ASP.NET Core) gọi `context.Response.Clear()` trước khi chuyển sang error handler, xóa mọi response header đã set — kể cả `X-Correlation-ID` từ `CorrelationIdMiddleware`. `GlobalExceptionHandler` đọc lại từ `HttpContext.Items["CorrelationId"]` và set lại header trước khi ghi response.

---

## 6. Packages được thêm

| Project | Package | Phiên bản | Publisher | Lý do |
|---------|---------|-----------|-----------|-------|
| `LuckyWheel.IntegrationTests` | `Microsoft.AspNetCore.Mvc.Testing` | `8.0.11` | Microsoft | Cần `WebApplicationFactory<Program>` để host API in-memory trong integration test |
| `LuckyWheel.IntegrationTests` | `Microsoft.Extensions.Configuration.UserSecrets` | `8.0.1` → `8.0.1` | Microsoft | Nâng từ `8.0.0` → `8.0.1` để giải quyết transitive dependency conflict từ `Mvc.Testing 8.0.11` → `Microsoft.Extensions.Hosting 8.0.1` |

**Không thêm package nào khác.** Tất cả exception types, validation, middleware, error handler đều dùng .NET 8 / ASP.NET Core built-in.

---

## 7. Kết quả build/test

### Build
```
dotnet restore   → PASS
dotnet build --no-restore → PASS (0 Error, 0 Warning)
```

### Test
```
dotnet test --no-restore

Passed!  - Failed: 0, Passed:  74, Total:  74  ← LuckyWheel.UnitTests.dll
Passed!  - Failed: 0, Passed:  32, Total:  32  ← LuckyWheel.IntegrationTests.dll
```

**Tổng: 106 tests, 0 failed, 0 skipped.**

#### Unit tests mới (Phase 4) — 42 tests
| Class | Số test |
|-------|---------|
| `ValidationResultTests` | 9 |
| `ExceptionTests` | 12 |
| `SystemClockTests` | 4 |
| `CorrelationIdMiddlewareTests` | 13 |

#### Integration tests mới (Phase 4) — 12 tests (trong `GlobalExceptionHandlerTests`)
| Test | Mô tả |
|------|-------|
| `ValidationException_Returns400_...` | HTTP 400, `VALIDATION_ERROR`, `errors`, `traceId` |
| `ValidationException_Errors_ContainFieldMessages` | Field-level errors đúng |
| `NotFoundException_Returns404_...` | HTTP 404, `NOT_FOUND` |
| `ConflictException_Returns409_...` | HTTP 409, `CONFLICT` |
| `BusinessRuleViolationException_Returns400_...` | HTTP 400, `BUSINESS_RULE_VIOLATION` |
| `DomainException_Returns400_...` | HTTP 400, `BUSINESS_RULE_VIOLATION` (tái dùng DomainException) |
| `UnhandledException_Returns500_...` | HTTP 500, không lộ stack trace / secret |
| `ErrorResponse_HasXCorrelationIdHeader` | Mọi error response có `X-Correlation-ID` |
| `SuccessResponse_HasXCorrelationIdHeader` | Mọi success response có `X-Correlation-ID` |
| `ClientSupplied_ValidCorrelationId_...` | Client trace id hợp lệ được echo lại |
| `ClientSupplied_TooLongCorrelationId_...` | ID dài > 64 bị bỏ qua, dùng system id |
| `HealthEndpoint_Returns200` | `/health` vẫn trả 200 |

#### Integration tests cũ (Phase 3) — 20 tests
Tất cả tiếp tục pass sau khi thêm Phase 4 code.

#### Note về SQL Server
- Các integration test Phase 3 (`DatabaseConstraintTests`, `EfModelMetadataTests`) yêu cầu SQL Server local (`LuckyWheelDb_IntegrationTests`). Chúng đã pass khi chạy trên máy có SQL Server.
- Các integration test Phase 4 (`GlobalExceptionHandlerTests`) **KHÔNG cần SQL Server** — sử dụng `WebApplicationFactory` in-memory với health check registrations bị xóa qua `HealthCheckServiceOptions`.

---

## 8. Những phần chưa làm — để lại cho giai đoạn sau

| Phần | Giai đoạn dự kiến |
|------|-------------------|
| `UNAUTHORIZED` (HTTP 401) | Giai đoạn JWT/Authentication |
| JWT middleware / login admin | Giai đoạn Authentication |
| CRUD Wheel, WheelVersion, Prize | Giai đoạn Application/API CRUD |
| Key generation API | Giai đoạn Key Management |
| Spin engine API | Giai đoạn Spin |
| Worker expire key (background job) | Giai đoạn Background Workers |
| Redemption / cancellation | Giai đoạn Redemption |
| Swagger response metadata cho error codes | Có thể bổ sung khi tạo endpoint thực |

---

## 9. Trạng thái giai đoạn

`COMPLETED`

- UTC xác minh: `2026-08-12T08:20:00Z`
- Build: PASS (0 error, 0 warning)
- Test: 106/106 passed
- Package mới: `Microsoft.AspNetCore.Mvc.Testing 8.0.11` (Microsoft, official)
- Không sửa Domain business rules
- Không triển khai authentication, Wheel management, key generation hay spin engine

---

## 10. Verification / Review

- **Những điểm đã kiểm tra**: 
  - Application không phụ thuộc `Microsoft.AspNetCore.*`.
  - Có implementation `IValidator<TRequest>` và `ValidationResult` thuần C#, hỗ trợ nhiều lỗi trên nhiều field.
  - Exception phân rã rõ ràng (`ValidationException`, `NotFoundException`, v.v.) và error code ổn định.
  - `GlobalExceptionHandler` mapping đúng HTTP status và Error Code, trả về RFC 7807 `ProblemDetails` với `traceId`, không lộ detail trên Production với lỗi 500.
  - `CorrelationIdMiddleware` xử lý header `X-Correlation-ID` an toàn và chuẩn xác.
  - `IClock` trả thời điểm UTC, đăng ký Singleton hợp lý.
  - API Health check hoạt động bình thường.
- **Những lỗi đã sửa**: Codebase ở trạng thái hoàn chỉnh, không tìm thấy lỗi cần phải fix. Mọi test đều pass từ đầu.
- **Package mới**: Không thêm package mới nào trong lần review này (chỉ có các package `Microsoft.AspNetCore.Mvc.Testing` và `Microsoft.Extensions.Configuration.UserSecrets` đã được khai báo trước đó cho Integration Tests).
- **Kết quả thật của build/test**:
  - `dotnet build --no-restore`: PASS (0 Error, 0 Warning)
  - `dotnet test --no-restore`: PASS (Total: 106, Passed: 106)
- **Các test chưa thể chạy và lý do**: Không có, tất cả các test đều có thể chạy và pass (bao gồm SQL Server integration test khi local có sẵn SQL Server).
- **Xác nhận không thao tác ngoài repository LuckyWheel**: Xác nhận tuyệt đối chỉ kiểm tra và thao tác (nếu có) trong giới hạn repository LuckyWheel.

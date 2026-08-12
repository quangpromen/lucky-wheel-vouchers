# Lucky Wheel Backend

Backend API cho hệ thống **Vòng Quay May Mắn (Lucky Wheel)** — nền tảng quản lý và vận hành vòng quay trúng thưởng.

## Mục tiêu

Xây dựng hệ thống backend hoàn chỉnh cho chương trình Lucky Wheel, bao gồm:

- Quản lý vòng quay và giải thưởng.
- Phân phối key cho người chơi.
- Engine quay ngẫu nhiên theo tỷ lệ cấu hình.
- Quản trị viên quản lý toàn bộ hệ thống.

## Công nghệ sử dụng

| Thành phần        | Công nghệ                          |
| ------------------ | ---------------------------------- |
| Framework          | ASP.NET Core 8 (Web API)           |
| Ngôn ngữ           | C# 12                              |
| Kiến trúc          | Clean Architecture, Modular Monolith |
| API Style          | RESTful (Controller-based)         |
| API Documentation  | Swagger / OpenAPI (Swashbuckle)     |
| Testing            | xUnit                              |
| Target Framework   | .NET 8 (`net8.0`)                  |

## Kiến trúc

Dự án tuân theo **Clean Architecture** với 4 layer chính:

```
LuckyWheel.Api              → Presentation Layer (Controllers, Middleware)
LuckyWheel.Application      → Application Layer (Use Cases, DTOs, Interfaces)
LuckyWheel.Domain           → Domain Layer (Entities, Value Objects, Domain Events)
LuckyWheel.Infrastructure   → Infrastructure Layer (Database, External Services)
```

**Quy tắc dependency:**

- `Domain` không tham chiếu project nào (lõi nghiệp vụ).
- `Application` chỉ tham chiếu `Domain`.
- `Infrastructure` tham chiếu `Application` và `Domain`.
- `Api` tham chiếu `Application` và `Infrastructure`.

## Cấu trúc project

```
LuckyWheel/
├── src/
│   ├── LuckyWheel.Api/                  # Web API (Controllers, Program.cs)
│   ├── LuckyWheel.Application/          # Application logic & interfaces
│   ├── LuckyWheel.Domain/               # Domain models & business rules
│   └── LuckyWheel.Infrastructure/       # Data access & external services
├── tests/
│   ├── LuckyWheel.UnitTests/            # Unit tests
│   └── LuckyWheel.IntegrationTests/     # Integration tests
├── .editorconfig
├── .gitignore
├── Directory.Build.props
├── LuckyWheel.sln
└── README.md
```

## Điều kiện môi trường

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) trở lên.
- IDE: Visual Studio 2022, VS Code, hoặc JetBrains Rider.
- (Giai đoạn sau) SQL Server cho database.

## Hướng dẫn sử dụng

### Restore packages

```bash
dotnet restore
```

### Build solution

```bash
dotnet build
```

### Chạy tests

```bash
dotnet test
```

### Chạy API

```bash
dotnet run --project src/LuckyWheel.Api
```

## Endpoints

| Method | Path               | Mô tả                          |
| ------ | ------------------ | ------------------------------- |
| GET    | `/health`          | Health Check                    |
| GET    | `/api/system/info` | Thông tin hệ thống              |
| GET    | `/swagger`         | Swagger UI (Development only)   |

## Các giai đoạn tiếp theo

- **Giai đoạn 2**: Domain Entities, Enums, Value Objects.
- **Giai đoạn 3**: Entity Framework Core, DbContext, Migrations.
- **Giai đoạn 4**: Repository Pattern, Unit of Work.
- **Giai đoạn 5**: Authentication (ASP.NET Core Identity, JWT).
- **Giai đoạn 6**: Wheel & Prize CRUD APIs.
- **Giai đoạn 7**: Key Generation & Distribution.
- **Giai đoạn 8**: Spin Engine (Random with weighted probability).
- **Giai đoạn 9**: Background Jobs, Caching, Optimization.
- **Giai đoạn 10**: Docker, CI/CD, Deployment.

## License

Private — All rights reserved.

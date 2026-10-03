# 🏛️ Barangay Information and Document Management System (BIDMS)

BIDMS is an ASP.NET Core MVC application designed to manage barangay records, handle resident information, issue official certificates, and log blotter incidents.

---

## 📊 System Architecture & Data Flow

```mermaid
flowchart TD
    A[User / Admin] -->|HTTP Request| B[ASP.NET Core MVC Controllers]
    B -->|Authenticate / Authorize| C[Account & Session Service]
    B -->|Business Logic| D[ApplicationDbContext / EF Core]
    D -->|SQL Queries| E[(MySQL Database: bdims_db)]
    
    subgraph Core Features
        B --> F[Resident Records Management]
        B --> G[Certificate & Document Generator]
        B --> H[Blotter & Incident Logging]
        B --> I[Announcements & Dashboard Analytics]
    end

    G -->|Render View| J[Razor Views / Bootstrap UI]
    F -->|Render View| J
    H -->|Render View| J

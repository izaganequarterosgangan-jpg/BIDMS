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
    H -->|Render View| JsequenceDiagram
    autonumber
    actor User as Resident / Admin
    participant Auth as Auth Controller
    participant Doc as Document Controller
    participant DB as MySQL Database

    User->>Auth: Login(Username, Password)
    Auth->>DB: Validate Credentials
    DB-->>Auth: Identity Validated
    Auth-->>User: Redirect to Dashboard

    User->>Doc: Request Certificate (e.g., Barangay Clearance)
    Doc->>DB: Fetch Resident Info
    DB-->>Doc: Return Resident Data
    Doc-->>User: Generate & Render Printable Document

# ✈️ Flight Assistant

[![.NET](https://img.shields.io/badge/.NET-10.0-blueviolet?logo=dotnet)](https://dotnet.microsoft.com/)  
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-blue?logo=postgresql)](https://www.postgresql.org/)  
[![Redis](https://img.shields.io/badge/Redis-Cache-red?logo=redis)](https://redis.io/)  
[![Azure](https://img.shields.io/badge/Azure-App%20Service-0089D6?logo=microsoft-azure)](https://azure.microsoft.com/)  

📚 Full documentation → [See Notion Wiki](https://www.notion.so/24c209410885800f9a9cf4f0b1a83d35?v=24c20941088580c596e0000ca2b0fc14&source=copy_link)


Flight Assistant is a **cross-platform travel companion app** that helps frequent flyers:  

- Track flights with live status updates  
- Manage alliance tiers and loyalty programs  
- Discover lounge eligibility and airport benefits  

The project starts with a **.NET 10 backend**, an **iOS client (SwiftUI)**, and will expand to **Android & Web**.  

---

## ⚡ Quick Start (local dev)

**Prerequisites:** Docker, .NET 10 SDK, `az login` (for Key Vault)

```bash
# 1. Start Postgres + Redis
docker compose up -d

# 2. Run the API (connects to localhost by default)
dotnet run --project FlightAssistant.API
```

Swagger UI → http://localhost:5000/swagger

**HTTP Client:** Open `FlightAssistant.API/api.http` in VS Code (REST Client extension) or JetBrains Rider. Paste a B2C JWT into `@token` and run any request with one click.

---

## 🚀 Tech Stack

**Backend**
- .NET 10 LTS (ASP.NET Core Web API, Clean Architecture, MediatR, EF Core)  
- PostgreSQL (primary storage)  
- Redis (caching)  
- Azure App Service (hosting)  
- Azure AD B2C / Entra External ID (authentication)  
- Azure Key Vault (secrets)  
- Azure App Insights (telemetry)  

**Clients**
- iOS: SwiftUI (Phase 1)  
- Android: Jetpack Compose (Phase 2)  
- Web: React/Next.js (Phase 3)  

**Data Sources**
- Flight Status → AeroDataBox / AviationStack  
- Alliances & Airports → OpenFlights + curated JSON  
- Lounge Info → curated static list  

---

## ✨ Features

### ✅ MVP
- Add flights manually (number + date)  
- Get **live flight status** updates  
- View **alliance tiers & benefits**  
- Lounge eligibility evaluator (tier + airline + terminal)  
- Push notifications for flight status changes (iOS / APNs)  
- Offline access with cached data  

### 🔮 Future
- Visa & entry requirements lookup  
- Auto-import flights from Gmail / Outlook  
- Sync loyalty accounts via AwardWallet  
- Higher-reliability APIs (FlightAware, Cirium)  
- Android & Web clients  
- Expanded lounge & airport database  

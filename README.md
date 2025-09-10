# ✈️ Flight Assistant

[![.NET](https://img.shields.io/badge/.NET-9.0-blueviolet?logo=dotnet)](https://dotnet.microsoft.com/)  
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-blue?logo=postgresql)](https://www.postgresql.org/)  
[![Redis](https://img.shields.io/badge/Redis-Cache-red?logo=redis)](https://redis.io/)  
[![Azure](https://img.shields.io/badge/Azure-App%20Service-0089D6?logo=microsoft-azure)](https://azure.microsoft.com/)  

Flight Assistant is a **cross-platform travel companion app** that helps frequent flyers:  

- Track flights with live status updates  
- Check visa & entry requirements  
- Manage alliance tiers and loyalty programs  
- Discover lounge eligibility and airport benefits  

The project starts with a **.NET 9 backend**, an **iOS client (SwiftUI)**, and will expand to **Android & Web**.  

---

## 🚀 Tech Stack

**Backend**
- .NET 9 (ASP.NET Core Web API, Clean Architecture, MediatR, EF Core)  
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
- Visa Rules → Sherpa API  
- Alliances & Airports → OpenFlights + curated JSON  
- Lounge Info → curated static list  

---

## ✨ Features

### ✅ MVP
- Add flights manually (number + date)  
- Get **live flight status** updates  
- Visa & entry requirements lookup  
- View **alliance tiers & benefits**  
- Lounge eligibility evaluator (tier + airline + terminal)  
- Push notifications for flight status changes (iOS / APNs)  
- Offline access with cached data  

### 🔮 Future
- Auto-import flights from Gmail / Outlook  
- Sync loyalty accounts via AwardWallet  
- Higher-reliability APIs (FlightAware, Cirium)  
- Android & Web clients  
- Expanded lounge & airport database  

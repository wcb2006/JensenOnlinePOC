# JENSENOnline – POC för säker systemutveckling

Proof of Concept för en e-handel med **frontend (Vanilla JS)**, **backend (ASP.NET Core Web API, .NET 10)** och **databas (SQLite)** med fokus på säkerhet.

```
JensenOnlinePOC/
├── src/JensenOnline.Api/        ASP.NET Core Web API + EF Core + SQLite  (https://localhost:7001)
├── src/JensenOnline.Web/        Vanilla JS-frontend + statisk värd med CSP (https://localhost:7002)
└── tests/JensenOnline.Api.Tests/  Automatiska säkerhetstester (attackscenarier)
```

## Kom igång

Krav: .NET 10 SDK.

```powershell
# 1. Lita på utvecklingscertifikatet för HTTPS (en gång)
dotnet dev-certs https --trust

# 2. Skapa en slumpad JWT-nyckel och spara den i user secrets (en gång, checkas aldrig in i git)
$b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
dotnet user-secrets set "Jwt:SigningKey" ([Convert]::ToBase64String($b)) --project src/JensenOnline.Api

# 3. Starta API:t (terminal 1) – databasen skapas och fylls med demodata automatiskt
dotnet run --project src/JensenOnline.Api

# 4. Starta frontenden (terminal 2) och öppna https://localhost:7002
dotnet run --project src/JensenOnline.Web

# 5. Kör säkerhetstesterna
dotnet test
```

Radera `src/JensenOnline.Api/jensenonline.db` för att återställa databasen.

### Demokonton (endast lokal utveckling, från `appsettings.Development.json`)

| Roll     | E-post                  | Lösenord              |
|----------|-------------------------|-----------------------|
| Admin    | admin@jensenonline.se   | `Styrelse#Server2026` |
| Kund     | kund@jensenonline.se    | `Kaffekopp#Bla2026`   |
| Kund 2   | kund2@jensenonline.se   | `Kaffekopp#Bla2026`   |

## Funktionalitet

| Område        | Kund                                   | Admin                                                       |
|---------------|----------------------------------------|-------------------------------------------------------------|
| Användare     | Registrering, login/logout             | Lista användare, ändra roll, spärra/aktivera konto          |
| Produkter     | Visa och söka                           | Skapa, ändra, ta bort (mjuk borttagning)                    |
| Order         | Skapa order från kundvagn, se egna order | Se alla order, ändra orderstatus                           |
| Säkerhet      | –                                      | Säkerhetslogg (audit log)                                   |

## API

| Metod  | Endpoint                         | Åtkomst       |
|--------|----------------------------------|---------------|
| POST   | `/api/auth/register`             | Anonym (rate limited) |
| POST   | `/api/auth/login`                | Anonym (rate limited) |
| POST   | `/api/auth/refresh`              | Refresh-cookie + CSRF-header |
| POST   | `/api/auth/logout`               | Refresh-cookie + CSRF-header |
| GET    | `/api/auth/me`                   | Inloggad      |
| GET    | `/api/products?search=`          | Anonym        |
| GET    | `/api/products/{id}`             | Anonym        |
| POST/PUT/DELETE | `/api/products[/{id}]`  | Admin         |
| POST   | `/api/orders`                    | Inloggad      |
| GET    | `/api/orders/mine`               | Inloggad      |
| GET    | `/api/orders/{id}`               | Ägare eller Admin |
| GET    | `/api/admin/orders`              | Admin         |
| PUT    | `/api/admin/orders/{id}/status`  | Admin         |
| GET    | `/api/admin/users`               | Admin         |
| PUT    | `/api/admin/users/{id}/role`     | Admin         |
| PUT    | `/api/admin/users/{id}/status`   | Admin         |
| GET    | `/api/admin/audit-logs`          | Admin         |

## Implementerade säkerhetsfunktioner

### G-krav

| Krav | Implementation | Fil |
|------|----------------|-----|
| Authentication | JWT (HS256) med validering av issuer, audience, signatur, livslängd och tillåten algoritm. Lösenord hashas med PBKDF2-HMAC-SHA512 (100 000 iterationer, unikt salt) via `PasswordHasher`. | `Program.cs`, `Security/TokenService.cs`, `Controllers/AuthController.cs` |
| Authorization, två roller | `Customer` och `Admin`. `[Authorize(Roles = "Admin")]` på adminfunktioner. **Deny by default**: fallback-policy kräver inloggning om inte `[AllowAnonymous]` anges. | `Program.cs`, `Controllers/*` |
| Skydd mot IDOR | Ägarkontroll i databasfrågan (`o.UserId == userId`), användar-id tas från token och aldrig från klienten. 404 i stället för 403 avslöjar inte att ordern finns. | `Controllers/OrdersController.cs` |
| Input validation | DataAnnotations på alla DTO:er (längd, intervall, vitlista med regex). **Okända JSON-fält avvisas** (skydd mot mass assignment). Separata DTO:er för in- och utdata. | `Dtos/Dtos.cs`, `Program.cs` |
| SQL injection | EF Core/LINQ ger alltid parametriserade frågor. Ingen rå SQL. | `Controllers/ProductsController.cs` |
| XSS | Frontend använder aldrig `innerHTML`, all data läggs in som textnoder. Strikt CSP utan inline-skript samt Trusted Types. API:t svarar bara med JSON och `nosniff`. | `wwwroot/js/dom.js`, `JensenOnline.Web/Program.cs` |
| Säker felhantering | Central `IExceptionHandler` loggar detaljer internt och returnerar ett generiskt ProblemDetails-svar med trace-id. Ingen stacktrace skickas till klienten. | `Security/GlobalExceptionHandler.cs` |
| Affärslogik | Priset hämtas från databasen, aldrig från klienten. Lagersaldo och antal (1–10) kontrolleras. | `Controllers/OrdersController.cs` |
| HTTPS | HTTPS-redirect och HSTS i produktion. | `Program.cs` |

### VG – ytterligare säkerhetsfunktioner

| Funktion | Varför den valdes | Implementation |
|----------|-------------------|----------------|
| **Rate limiting** | Skyddar mot brute force, API abuse och överbelastning (DoS). Inbyggt i .NET och därför billigt. | Globalt 200 anrop/min per IP, login och registrering 10/min per IP. Svarar 429 med `Retry-After`. |
| **Account lockout** | Stoppar lösenordsgissning mot ett specifikt konto även från många IP-adresser. | Låses i 15 min efter 5 misslyckade försök. Rätt lösenord avvisas under låsningen (423). |
| **Password policy** | Svaga lösenord är den vanligaste vägen till kontokapning. | Minst 12 tecken, versal, gemen, siffra och specialtecken. Lösenordet får inte innehålla e-postadressen och får inte finnas på listan över vanliga lösenord. |
| **Säker tokenhantering + refresh tokens** | Stulna tokens: kort livslängd begränsar skadan, och en HttpOnly-cookie kan inte läsas via XSS. | Access token gäller i 15 min och finns bara i JS-minnet. Refresh token ligger i en `HttpOnly; Secure; SameSite=Strict`-cookie, lagras hashad i databasen och **roteras** vid varje användning. **Återanvändning upptäcks** och spärrar alla sessioner. |
| **Security stamp** | Rolländring eller spärrat konto ska gälla direkt, inte först när token går ut. | Varje request kontrollerar att tokenens `sstamp` matchar databasen och att kontot inte är spärrat. |
| **Audit- och säkerhetsloggning** | Upptäckt och spårbarhet vid incidenter och insider threats. | Inloggningar, misslyckade försök, låsningar, `AccessDenied` (403), IDOR-försök, admin-ändringar och token-återanvändning loggas. Loggen visas i adminpanelen och går även till `ILogger` (för SIEM). Kontrolltecken rensas bort för att förhindra log injection. |
| **CORS-policy** | Bara den egna frontenden får anropa API:t från webbläsaren. | Endast `https://localhost:7002`, bestämda metoder och headers. |
| **CSRF-skydd** | Endpoints som använder cookies (refresh/logout) måste skyddas. | `SameSite=Strict` samt en obligatorisk egen header (`X-Requested-With`) som kräver CORS-preflight. |
| **Security headers** | Skydd på djupet (defense in depth) mot XSS, clickjacking och MIME-sniffing. | CSP, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`, `Cache-Control: no-store`. |
| **Central error handling** | Förhindrar informationsläckage. | Se G-krav. |
| **Övrigt** | – | Max 64 KB request body, `Server`-headern är borttagen, `AllowedHosts` är begränsat, konfigurationen valideras vid start (fail fast om JWT-nyckeln saknas), och user enumeration förhindras (samma felmeddelande och samma svarstid oavsett om kontot finns). |

## Säkerhetstester

`dotnet test` kör 28 automatiska attackscenarier (`tests/JensenOnline.Api.Tests/SecurityTests.cs`):

| # | Scenario | Förväntat resultat |
|---|----------|--------------------|
| 1 | Anonym användare anropar skyddade endpoints | 401 |
| 2 | Kund anropar adminfunktioner (users, orders, skapa/ta bort produkt) | 403 |
| 3 | Kund försöker ge sig själv admin-roll | 403 |
| 4 | Manipulerad JWT (roll ändrad till Admin) | 401 (ogiltig signatur) |
| 5 | JWT med `alg: none` | 401 |
| 6 | Spärrad användares befintliga token och refresh token | 401 direkt |
| 7 | IDOR: kund 2 läser kund 1:s order genom att byta ID | 404, och ordern syns inte i den egna listan |
| 8 | Ogiltiga produktdata (tomt, negativt, för långt, `<script>`, fel typ) | 400 |
| 9 | Mass assignment: `"role": "Admin"` vid registrering | 400, inget konto skapas |
| 10 | Klienten skickar eget pris (`unitPrice`) | 400, servern räknar priset |
| 11 | Orimliga antal (0, -1, 11, 1 000 000) | 400 |
| 12 | Svaga lösenord | 400 |
| 13 | XSS-payload i produktbeskrivning | Lagras som text och serveras som JSON med `nosniff` + CSP |
| 14 | SQL injection i sök (4 varianter, inkl. `DROP TABLE` och `UNION SELECT`) | 200, tom lista, tabellen intakt |
| 15 | Brute force: 5 felaktiga lösenord följt av rätt lösenord | Kontot låst (423) |
| 16 | User enumeration: befintligt jämfört med okänt konto | Identiskt svar |
| 17 | Stulen refresh token återanvänds efter rotation | 401 och alla sessioner spärras |
| 18 | Refresh utan CSRF-header | 403 |
| 19 | Refresh-cookiens attribut | `HttpOnly`, `Secure`, `SameSite=Strict` |
| 20 | Utloggning återkallar refresh token | 401 vid återanvändning |
| 21 | CORS-preflight från okänt origin | Ingen `Access-Control-Allow-Origin` |
| 22 | Felaktig JSON | 400 utan stacktrace eller interna detaljer |
| 23 | IDOR-försök loggas | `OrderAccessDenied` finns i audit-loggen |
| 24 | Brute force mot login-endpoint (rate limit) | 429 efter gränsen |

För manuella demos finns `src/JensenOnline.Api/JensenOnline.Api.http` med färdiga attackanrop.

### Demo i webbläsaren

1. **XSS:** Logga in som admin och skapa en produkt med beskrivningen `<img src=x onerror=alert(1)>`. Den visas som text och ingen popup visas.
2. **Adminskydd:** Logga in som kund och gå till `#/admin`. Sidan visar "Åtkomst nekad", och API:t returnerar 403 även vid direkta anrop.
3. **Account lockout:** Logga in med fel lösenord fem gånger. Därefter avvisas även rätt lösenord, och admin ser händelsen i Säkerhetsloggen.
4. **Token-ogiltigförklaring:** Spärra en inloggad kund som admin. Kundens nästa anrop avvisas direkt.

## Begränsningar (POC)

- Betalning via Klarna är mockad. Ordern skapas med status `Created`.
- JWT-nyckeln ligger lokalt i .NET user secrets och finns inte i repot. Demolösenorden ligger i `appsettings.Development.json` för att demon ska vara enkel. I produktion hämtas hemligheter från secrets management (t.ex. Azure Key Vault).
- MFA är inte implementerat, men rekommenderas starkt för administratörer.
- SQLite och en enda process. Rate limiting sker i minnet (per instans). I produktion används en API Gateway eller WAF med distribuerad rate limiting och `ForwardedHeaders` för korrekt klient-IP.
- Lagersaldot har ingen optimistisk samtidighetskontroll.

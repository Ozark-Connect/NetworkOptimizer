# JWT Authentication Migration Plan

## Overview

Migrate from HTTP Basic Authentication to JWT-based authentication with a proper login page. This provides better UX (no browser popup), password manager compatibility, and session management.

---

## Current State

- **Auth Method**: HTTP Basic Auth via middleware in `Program.cs`
- **Password Storage**: `AdminAuthService` with priority: Database > ENV var > Auto-generated
- **Session**: Browser-managed (credentials cached until browser close)
- **Login UI**: Browser-native dialog (not password manager friendly)

---

## Target State

- **Auth Method**: JWT Bearer tokens stored in HttpOnly cookies
- **Password Storage**: Same `AdminAuthService` (no changes needed)
- **Session**: Configurable expiration with refresh tokens
- **Login UI**: Blazor login page with form inputs

---

## Architecture

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│   Login Page    │────▶│  AuthController  │────▶│ AdminAuthService│
│  (Login.razor)  │     │  (POST /login)   │     │ (validate pwd)  │
└─────────────────┘     └──────────────────┘     └─────────────────┘
                               │
                               ▼
                        ┌──────────────────┐
                        │   JwtService     │
                        │ (generate token) │
                        └──────────────────┘
                               │
                               ▼
                        ┌──────────────────┐
                        │  HttpOnly Cookie │
                        │  (auth_token)    │
                        └──────────────────┘
```

---

## New Files to Create

| File | Purpose |
|------|---------|
| `Services/JwtService.cs` | Token generation, validation, refresh |
| `Controllers/AuthController.cs` | Login/logout API endpoints |
| `Components/Pages/Login.razor` | Login form UI |
| `Components/Layout/AuthLayout.razor` | Layout for unauthenticated pages |
| `Services/JwtAuthStateProvider.cs` | Blazor auth state integration |

---

## Files to Modify

| File | Changes |
|------|---------|
| `Program.cs` | Replace Basic Auth middleware with JWT auth |
| `appsettings.json` | Add JWT configuration section |
| `Components/Layout/MainLayout.razor` | Add logout button, auth checks |
| `Components/Layout/NavMenu.razor` | Show/hide based on auth state |

---

## Implementation Details

### 1. JWT Configuration (appsettings.json)

```json
{
  "Jwt": {
    "SecretKey": "auto-generated-on-first-run",
    "Issuer": "NetworkOptimizer",
    "Audience": "NetworkOptimizer",
    "TokenExpirationMinutes": 60,
    "RefreshTokenExpirationDays": 7,
    "CookieName": "auth_token"
  }
}
```

### 2. JwtService.cs

```csharp
public class JwtService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<JwtService> _logger;

    public JwtService(IConfiguration configuration, ILogger<JwtService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string GenerateToken(string username = "admin")
    {
        var key = GetOrCreateSecretKey();
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(
                int.Parse(_configuration["Jwt:TokenExpirationMinutes"] ?? "60")),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var key = GetOrCreateSecretKey();
        var tokenHandler = new JwtSecurityTokenHandler();

        try
        {
            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidateIssuer = true,
                ValidIssuer = _configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _configuration["Jwt:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out _);

            return principal;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Token validation failed");
            return null;
        }
    }

    private string GetOrCreateSecretKey()
    {
        // Store in database or generate deterministically from machine key
        // Minimum 256 bits (32 bytes) for HMAC-SHA256
    }
}
```

### 3. AuthController.cs

```csharp
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AdminAuthService _authService;
    private readonly JwtService _jwtService;
    private readonly ILogger<AuthController> _logger;

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var isValid = await _authService.ValidatePasswordAsync(request.Password);

        if (!isValid)
        {
            _logger.LogWarning("Failed login attempt from {IP}",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { message = "Invalid password" });
        }

        var token = _jwtService.GenerateToken();

        // Set HttpOnly cookie
        Response.Cookies.Append("auth_token", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddMinutes(60)
        });

        _logger.LogInformation("Successful login from {IP}",
            HttpContext.Connection.RemoteIpAddress);

        return Ok(new { message = "Login successful" });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("auth_token");
        return Ok(new { message = "Logged out" });
    }

    [HttpGet("check")]
    public IActionResult CheckAuth()
    {
        return User.Identity?.IsAuthenticated == true
            ? Ok(new { authenticated = true })
            : Unauthorized();
    }
}

public record LoginRequest(string Password);
```

### 4. Login.razor

```razor
@page "/login"
@layout AuthLayout
@inject NavigationManager Navigation
@inject HttpClient Http

<div class="login-container">
    <div class="login-card">
        <div class="login-header">
            <img src="/images/logo.png" alt="Network Optimizer" />
            <h1>Network Optimizer</h1>
        </div>

        @if (!string.IsNullOrEmpty(errorMessage))
        {
            <div class="alert alert-danger">@errorMessage</div>
        }

        <EditForm Model="loginModel" OnValidSubmit="HandleLogin">
            <div class="form-group">
                <label>Password</label>
                <input type="password"
                       class="form-control"
                       @bind="loginModel.Password"
                       @bind:event="oninput"
                       placeholder="Enter admin password"
                       autofocus />
            </div>

            <button type="submit" class="btn btn-primary w-100" disabled="@isLoading">
                @if (isLoading)
                {
                    <span class="spinner-border spinner-border-sm"></span>
                }
                else
                {
                    <span>Sign In</span>
                }
            </button>
        </EditForm>

        <div class="login-footer">
            <small class="text-muted">
                @passwordSourceHint
            </small>
        </div>
    </div>
</div>

@code {
    private LoginModel loginModel = new();
    private string? errorMessage;
    private string? passwordSourceHint;
    private bool isLoading;

    protected override async Task OnInitializedAsync()
    {
        // Show hint about password source (env var, database, auto-generated)
        // Fetch from API endpoint
    }

    private async Task HandleLogin()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            var response = await Http.PostAsJsonAsync("/api/auth/login", loginModel);

            if (response.IsSuccessStatusCode)
            {
                Navigation.NavigateTo("/", forceLoad: true);
            }
            else
            {
                errorMessage = "Invalid password";
            }
        }
        catch (Exception ex)
        {
            errorMessage = "Login failed. Please try again.";
        }
        finally
        {
            isLoading = false;
        }
    }

    private class LoginModel
    {
        public string Password { get; set; } = "";
    }
}
```

### 5. Program.cs Changes

```csharp
// Remove Basic Auth middleware

// Add JWT Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            // Read token from cookie instead of Authorization header
            context.Token = context.Request.Cookies["auth_token"];
            return Task.CompletedTask;
        }
    };

    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});

builder.Services.AddAuthorization();
builder.Services.AddScoped<JwtService>();

// In middleware pipeline
app.UseAuthentication();
app.UseAuthorization();

// Redirect unauthenticated to login
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower() ?? "";
    var isAuthEndpoint = path.StartsWith("/api/auth") ||
                         path.StartsWith("/login") ||
                         path.StartsWith("/_blazor") ||
                         path.StartsWith("/_framework") ||
                         path.StartsWith("/css") ||
                         path.StartsWith("/js");

    if (!isAuthEndpoint && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.Redirect("/login");
        return;
    }

    await next();
});
```

---

## Login Page Styling

```css
/* wwwroot/css/login.css */
.login-container {
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
    background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
}

.login-card {
    background: #fff;
    border-radius: 12px;
    padding: 2rem;
    width: 100%;
    max-width: 400px;
    box-shadow: 0 10px 40px rgba(0, 0, 0, 0.3);
}

.login-header {
    text-align: center;
    margin-bottom: 2rem;
}

.login-header img {
    width: 80px;
    margin-bottom: 1rem;
}

.login-header h1 {
    font-size: 1.5rem;
    color: #333;
    margin: 0;
}

.login-footer {
    text-align: center;
    margin-top: 1.5rem;
    padding-top: 1rem;
    border-top: 1px solid #eee;
}
```

---

## Security Considerations

### Token Storage
- **HttpOnly cookie**: Prevents XSS attacks from accessing token
- **Secure flag**: Only sent over HTTPS
- **SameSite=Strict**: Prevents CSRF attacks

### Secret Key Management
- Generate 256-bit key on first run
- Store in database (encrypted) or derive from machine key
- Rotate periodically if needed

### Rate Limiting (Optional)
```csharp
// Add to Program.cs for brute force protection
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 5;
    });
});
```

---

## Migration Steps

### Phase 1: Core Infrastructure
1. [ ] Create `JwtService.cs`
2. [ ] Create `AuthController.cs`
3. [ ] Add JWT NuGet package: `Microsoft.AspNetCore.Authentication.JwtBearer`
4. [ ] Update `appsettings.json`

### Phase 2: UI Components
5. [ ] Create `AuthLayout.razor`
6. [ ] Create `Login.razor`
7. [ ] Add login page CSS

### Phase 3: Integration
8. [ ] Update `Program.cs` - replace Basic Auth with JWT
9. [ ] Add logout button to `MainLayout.razor`
10. [ ] Update `NavMenu.razor` with auth state

### Phase 4: Testing
11. [ ] Test login flow
12. [ ] Test token expiration
13. [ ] Test logout
14. [ ] Test password manager auto-fill
15. [ ] Test redirect to login when unauthenticated

### Phase 5: Cleanup
16. [ ] Remove Basic Auth middleware code
17. [ ] Update documentation

---

## Rollback Plan

If issues arise, revert by:
1. Restore Basic Auth middleware in `Program.cs`
2. Remove JWT authentication configuration
3. The `AdminAuthService` remains unchanged, so password management still works

---

## Estimated Effort

| Phase | Time |
|-------|------|
| Core Infrastructure | 1-2 hours |
| UI Components | 1 hour |
| Integration | 1-2 hours |
| Testing | 1 hour |
| **Total** | **4-6 hours** |

---

## Dependencies

```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.0.0" />
```

---

## Future Enhancements

- **Remember Me**: Extend token expiration with checkbox
- **Session Timeout Warning**: Prompt before token expires
- **Multiple Admin Users**: Add user table with roles
- **2FA**: TOTP-based two-factor authentication
- **Audit Log**: Track login/logout events

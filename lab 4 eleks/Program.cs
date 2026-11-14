using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Отримуємо рядок підключення до БД Neon (підтримує і формат Host=..., і формат postgresql://...)
var rawConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                          ?? Environment.GetEnvironmentVariable("DATABASE_URL");

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (!string.IsNullOrWhiteSpace(rawConnectionString))
    {
        options.UseNpgsql(NormalizePostgresConnectionString(rawConnectionString));
    }
    else
    {
        options.UseInMemoryDatabase("AppDb");
    }
});

builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<IdentityUser>()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Введіть токен доступу (без слова Bearer)"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddValidatorsFromAssemblyContaining<CreateOrderDtoValidator>();

var app = builder.Build();

// Автоматичне створення таблиць у базі даних Neon при запуску контейнера
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Попередження ініціалізації БД: {ex.Message}");
    }
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityApi<IdentityUser>();
app.MapControllers();
app.MapGet("/", () => "Сервер успішно задеплоєно! Додано Neon PostgreSQL, Render CD, Unit Tests та FluentValidation. Перейдіть на /swagger");

app.Run();

static string NormalizePostgresConnectionString(string raw)
{
    raw = raw.Trim().Trim('"', '\'');
    if (raw.StartsWith("psql ", StringComparison.OrdinalIgnoreCase))
        raw = raw.Substring(5).Trim().Trim('"', '\'');

    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var db = uri.AbsolutePath.TrimStart('/');
        var port = uri.Port > 0 ? uri.Port : 5432;
        return $"Host={uri.Host};Port={port};Database={db};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=True";
    }

    return raw;
}

// =====================================================================
// 1. ШАР ДАНИХ (DAL)
// =====================================================================
public class Order
{
    public int Id { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal TotalPrice { get; set; }
    public string UserId { get; set; } = string.Empty;
}

public class AppDbContext : IdentityDbContext<IdentityUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Order> Orders => Set<Order>();
}

// =====================================================================
// 2. КАСТОМНІ ВИКЛЮЧЕННЯ (Custom Exceptions)
// =====================================================================
public class CustomNotFoundException : Exception
{
    public CustomNotFoundException(string message) : base(message) { }
}

public class BusinessRuleException : Exception
{
    public BusinessRuleException(string message) : base(message) { }
}

// =====================================================================
// 3. FLUENT VALIDATION ДЛЯ DTO
// =====================================================================
public record CreateOrderDto(string ProductName, int Quantity, decimal UnitPrice);
public record OrderDto(int Id, string ProductName, int Quantity, decimal TotalPrice);

public class CreateOrderDtoValidator : AbstractValidator<CreateOrderDto>
{
    public CreateOrderDtoValidator()
    {
        RuleFor(x => x.ProductName)
            .NotEmpty().WithMessage("Назва товару не може бути порожньою.")
            .MinimumLength(3).WithMessage("Назва має містити мінімум 3 символи.");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Кількість має бути більшою за нуль.");

        RuleFor(x => x.UnitPrice)
            .GreaterThan(0).WithMessage("Ціна має бути більшою за нуль.");
    }
}

// =====================================================================
// 4. GLOBAL EXCEPTION HANDLER MIDDLEWARE
// =====================================================================
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    public GlobalExceptionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            context.Response.ContentType = "application/json";
            var response = new { error = ex.Message, details = "" };

            switch (ex)
            {
                case ValidationException validationEx:
                    context.Response.StatusCode = 400;
                    var errors = validationEx.Errors.Select(e => e.ErrorMessage);
                    response = new { error = "Помилка валідації DTO", details = string.Join("; ", errors) };
                    break;
                case BusinessRuleException:
                    context.Response.StatusCode = 400;
                    break;
                case CustomNotFoundException:
                    context.Response.StatusCode = 404;
                    break;
                default:
                    context.Response.StatusCode = 500;
                    response = new { error = "Внутрішня помилка сервера", details = ex.Message };
                    break;
            }

            await context.Response.WriteAsJsonAsync(response);
        }
    }
}

// =====================================================================
// 5. ШАР БІЗНЕС-ЛОГІКИ (BLL)
// =====================================================================
public interface IOrderService
{
    Task<List<OrderDto>> GetUserOrdersAsync(string userId);
    Task<OrderDto> GetOrderByIdAsync(int id, string userId);
    Task<OrderDto> CreateOrderAsync(CreateOrderDto dto, string userId);
}

public class OrderService : IOrderService
{
    private readonly AppDbContext _db;
    public OrderService(AppDbContext db) => _db = db;

    public async Task<List<OrderDto>> GetUserOrdersAsync(string userId)
    {
        return await _db.Orders
            .Where(o => o.UserId == userId)
            .Select(o => new OrderDto(o.Id, o.ProductName, o.Quantity, o.TotalPrice))
            .ToListAsync();
    }

    public async Task<OrderDto> GetOrderByIdAsync(int id, string userId)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id && o.UserId == userId);
        if (order == null)
            throw new CustomNotFoundException($"Замовлення з ID {id} не знайдено.");

        return new OrderDto(order.Id, order.ProductName, order.Quantity, order.TotalPrice);
    }

    public async Task<OrderDto> CreateOrderAsync(CreateOrderDto dto, string userId)
    {
        if (dto.Quantity <= 0 || dto.UnitPrice <= 0)
            throw new BusinessRuleException("Кількість та ціна мають бути більшими за 0.");

        decimal total = dto.Quantity * dto.UnitPrice;
        if (dto.Quantity >= 5) total *= 0.9m;

        var order = new Order
        {
            ProductName = dto.ProductName,
            Quantity = dto.Quantity,
            TotalPrice = total,
            UserId = userId
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        return new OrderDto(order.Id, order.ProductName, order.Quantity, order.TotalPrice);
    }
}

// =====================================================================
// 6. КОНТРОЛЕР
// =====================================================================
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;
    public OrdersController(IOrderService orderService) => _orderService = orderService;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _orderService.GetUserOrdersAsync(UserId));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id) =>
        Ok(await _orderService.GetOrderByIdAsync(id, UserId));

    [HttpPost]
    public async Task<IActionResult> Create(CreateOrderDto dto, [FromServices] IValidator<CreateOrderDto> validator)
    {
        var validationResult = await validator.ValidateAsync(dto);
        if (!validationResult.IsValid)
            throw new ValidationException(validationResult.Errors);

        return Ok(await _orderService.CreateOrderAsync(dto, UserId));
    }
}
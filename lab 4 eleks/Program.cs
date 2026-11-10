using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Xunit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("AppDb"));
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

app.UseSwagger();
app.UseSwaggerUI();

app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityApi<IdentityUser>();
app.MapControllers();
app.MapGet("/", () => "Сервер працює! Додано Custom Exceptions, FluentValidation та Unit Tests.");

app.Run();

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
        if (dto.Quantity >= 5) total *= 0.9m; // 10% знижка при замовленні від 5 шт.

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

// =====================================================================
// 7. UNIT TESTS (xUnit) — Покриття BLL, Custom Exceptions та FluentValidation
// =====================================================================
public class OrderServiceAndValidatorTests
{
    private static AppDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task CreateOrderAsync_Applies10PercentDiscount_WhenQuantityIs5OrMore()
    {
        using var db = GetInMemoryDbContext();
        var service = new OrderService(db);
        var dto = new CreateOrderDto("Gaming Laptop", 5, 1000m);

        var result = await service.CreateOrderAsync(dto, "user-123");

        // 5 * 1000 = 5000, зі знижкою 10% має бути 4500
        Assert.Equal(4500m, result.TotalPrice);
    }

    [Fact]
    public async Task CreateOrderAsync_DoesNotApplyDiscount_WhenQuantityIsLessThan5()
    {
        using var db = GetInMemoryDbContext();
        var service = new OrderService(db);
        var dto = new CreateOrderDto("Mouse", 2, 500m);

        var result = await service.CreateOrderAsync(dto, "user-123");

        Assert.Equal(1000m, result.TotalPrice);
    }

    [Fact]
    public async Task CreateOrderAsync_ThrowsBusinessRuleException_WhenQuantityIsZeroOrLess()
    {
        using var db = GetInMemoryDbContext();
        var service = new OrderService(db);
        var dto = new CreateOrderDto("Keyboard", 0, 500m);

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateOrderAsync(dto, "user-123"));
    }

    [Fact]
    public async Task GetOrderByIdAsync_ThrowsCustomNotFoundException_WhenOrderDoesNotExist()
    {
        using var db = GetInMemoryDbContext();
        var service = new OrderService(db);

        await Assert.ThrowsAsync<CustomNotFoundException>(() => service.GetOrderByIdAsync(999, "user-123"));
    }

    [Fact]
    public void Validator_ReturnsErrors_WhenDtoIsInvalid()
    {
        var validator = new CreateOrderDtoValidator();
        var invalidDto = new CreateOrderDto("AB", 0, -50m); // Назва < 3 символів, кількість 0, ціна від'ємна

        var result = validator.Validate(invalidDto);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
    }
}
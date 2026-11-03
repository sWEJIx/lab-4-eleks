using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("AppDb"));
builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<IdentityUser>()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ДОДАНО: Реєстрація всіх валідаторів FluentValidation
builder.Services.AddValidatorsFromAssemblyContaining<CreateOrderDtoValidator>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// ДОДАНО: Глобальний обробник помилок (має стояти на самому початку пайплайну!)
app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityApi<IdentityUser>();
app.MapControllers();
app.MapGet("/", () => "Сервер працює! Додано Custom Exceptions та FluentValidation.");

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
// 2. КАСТОМНІ ВИКЛЮЧЕННЯ (Custom Exceptions - ДЗ)
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
// 3. FLUENT VALIDATION ДЛЯ DTO (ДЗ)
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
// 4. GLOBAL EXCEPTION HANDLER MIDDLEWARE (ДЗ)
// =====================================================================
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;

    public GlobalExceptionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            // Пропускаємо запит далі до контролерів
            await _next(context);
        }
        catch (Exception ex)
        {
            // Якщо десь випала помилка - перехоплюємо її тут
            context.Response.ContentType = "application/json";
            var response = new { error = ex.Message, details = "" };

            switch (ex)
            {
                case ValidationException validationEx:
                    context.Response.StatusCode = 400; // BadRequest
                    var errors = validationEx.Errors.Select(e => e.ErrorMessage);
                    response = new { error = "Помилка валідації DTO", details = string.Join("; ", errors) };
                    break;
                case BusinessRuleException:
                    context.Response.StatusCode = 400; // BadRequest
                    break;
                case CustomNotFoundException:
                    context.Response.StatusCode = 404; // NotFound
                    break;
                default:
                    context.Response.StatusCode = 500; // InternalServerError
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

    public async Task<OrderDto> CreateOrderAsync(CreateOrderDto dto, string userId)
    {
        // Використовуємо наше кастомне виключення
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
// 6. КОНТРОЛЕР (Ідеально чистий - жодних try-catch!)
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

    [HttpPost]
    public async Task<IActionResult> Create(CreateOrderDto dto, [FromServices] IValidator<CreateOrderDto> validator)
    {
        // 1. Валідуємо DTO через FluentValidation
        var validationResult = await validator.ValidateAsync(dto);
        if (!validationResult.IsValid)
        {
            // Якщо є помилки (наприклад, порожня назва), викидаємо Exception.
            // Його зловить наш GlobalExceptionMiddleware і красиво поверне клієнту!
            throw new ValidationException(validationResult.Errors);
        }

        // 2. Якщо валідація пройшла успішно - просто виконуємо бізнес-логіку
        return Ok(await _orderService.CreateOrderAsync(dto, UserId));
    }
}
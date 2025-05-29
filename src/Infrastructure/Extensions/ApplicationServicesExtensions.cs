using ecommerce.Services;
using Microsoft.Extensions.DependencyInjection;
using ecommerce.Contracts;
using ecommerce.Webhooks;
using ecommerce.Mappings;
public static class ApplicationServicesExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddAutoMapper(typeof(MappingProfile))
            .AddMemoryCache()
            .AddScoped<PaymentSessionStrategy>()
            .AddScoped<AuthService>()
            .AddScoped<SubscriptionSessionStrategy>()
            .AddScoped<IPaymentProcessingService, PaymentProcessingService>()
            .AddScoped<ISubscriptionProcessingService, SubscriptionProcessingService>()
            .AddScoped<ITelemetryService, ConsoleTelemetryService>()
            .AddScoped<IFileValidator, FileValidator>()
            .AddScoped<IContentStorageService, ContentStorageService>()
            .AddScoped<IChunkedUploadService, ChunkedUploadService>()
            .AddScoped<ICurrentUserService, CurrentUserService>();




        return services;
    }
}

using KerberosSmb;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.WriteIndented = builder.Environment.IsDevelopment();
});
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "KerberosSmbDemo", Version = "v1" });
});

builder.Services.AddOptions<KerberosSmbOptions>()
    .Configure(o =>
    {
        o.Server = builder.Configuration.GetValue<string>("SMB_SERVER") ?? string.Empty;
        o.Share = builder.Configuration.GetValue<string>("SMB_SHARE") ?? string.Empty;
        o.Spn = builder.Configuration.GetValue<string>("SMB_SPN") ?? string.Empty;
    })
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<KerberosSmbOptions>, KerberosSmbOptions.Validator>();
builder.Services.AddSingleton<KerberosSmbClient>();

builder.Services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.All);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDeveloperExceptionPage();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "KerberosSmbDemo v1"));
app.UseRouting();
app.MapControllers();

await app.RunAsync();

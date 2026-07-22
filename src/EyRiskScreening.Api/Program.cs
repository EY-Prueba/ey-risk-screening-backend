using EyRiskScreening.Application;
using EyRiskScreening.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

_ = typeof(ApplicationAssemblyMarker);
_ = typeof(InfrastructureAssemblyMarker);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();

public partial class Program;

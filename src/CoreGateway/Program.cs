using CoreGateway;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCoreGateway(builder.Configuration);
var app = builder.Build();
app.MapCoreGateway();
app.Run();

using SupervisorOperator;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSupervisorApi(builder.Configuration);
var app = builder.Build();
app.MapSupervisorApi();
app.Run();

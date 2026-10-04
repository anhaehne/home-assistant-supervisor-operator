using SupervisorOperator;
using SupervisorOperator.Foundation;
using SupervisorOperator.Lifecycle;

var builder = WebApplication.CreateBuilder(args);
if (args.Contains("--shutdown"))
{
    await ShutdownHook.Run(builder.Configuration);
    return;
}
builder.Services.AddSupervisorApi(builder.Configuration);
builder.Services.AddOperatorFoundation(builder.Configuration);
builder.Services.AddLifecycleOperator(builder.Configuration);
var app = builder.Build();
app.MapSupervisorApi();
app.Run();

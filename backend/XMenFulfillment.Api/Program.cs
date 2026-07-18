using OpenAI;
using XMenFulfillment.Api.Agents;
using XMenFulfillment.Api.Hubs;
using XMenFulfillment.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ── OpenAI client ─────────────────────────────────────────────────────────────
var openAiKey = builder.Configuration["OpenAI:ApiKey"]
    ?? throw new InvalidOperationException("OpenAI:ApiKey is not configured. Add it to appsettings.Development.json.");

builder.Services.AddSingleton(new OpenAIClient(openAiKey));

// ── Worker agents (registered as IAgent) ─────────────────────────────────────
builder.Services.AddSingleton<IAgent, CyclopsAgent>();
builder.Services.AddSingleton<IAgent, BeastAgent>();
builder.Services.AddSingleton<IAgent, WolverineAgent>();
builder.Services.AddSingleton<IAgent, GambitAgent>();
builder.Services.AddSingleton<IAgent, StormAgent>();
builder.Services.AddSingleton<IAgent, JeanGreyAgent>();

// ── Cerebro orchestrator + real-time broadcaster ────────────────────────────
builder.Services.AddScoped<Cerebro>();
builder.Services.AddScoped<AgentBroadcaster>();

// ── In-memory stores ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<InventoryStore>();

// ── API infrastructure ────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();
app.UseHttpsRedirection();
app.MapControllers();
app.MapHub<AgentHub>("/hubs/agents");

app.Run();

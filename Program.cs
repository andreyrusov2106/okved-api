using Microsoft.Data.Sqlite;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "Введите API-ключ в поле: X-API-Key",
        Name = "X-API-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "ApiKeyScheme"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            new List<string>()
        }
    });
});

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();

string connectionString = "Data Source=okved.db";
string validApiKey = "a6ffc074-757a-4cb4-9778-220599824bbd"; // придумайте сложнее

bool IsValidApiKey(HttpRequest request)
{
    if (request.Headers.TryGetValue("X-API-Key", out var apiKey))
        return apiKey == validApiKey;
    return false;
}

app.MapGet("/api/okved/{code}", async (string code, HttpRequest request) =>
{
    if (!IsValidApiKey(request)) return Results.Unauthorized();

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    var cmd = new SqliteCommand("SELECT name FROM okved2 WHERE code = @code", connection);
    cmd.Parameters.AddWithValue("@code", code);
    var name = await cmd.ExecuteScalarAsync();
    return name != null ? Results.Ok(new { code, name = name.ToString() }) : Results.NotFound();
});

app.MapGet("/api/okved/search", async (string q, HttpRequest request) =>
{
    if (!IsValidApiKey(request)) return Results.Unauthorized();

    string qLower = q.ToLowerInvariant();
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    var cmd = new SqliteCommand("SELECT code, name FROM okved2 WHERE name_lower LIKE @q LIMIT 20", connection);
    cmd.Parameters.AddWithValue("@q", $"%{qLower}%");
    var results = new List<object>();
    var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        results.Add(new { code = reader.GetString(0), name = reader.GetString(1) });
    return Results.Ok(results);
});

app.Run();
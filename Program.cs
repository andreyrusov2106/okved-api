using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();

string connectionString = "Data Source=okved.db";
string adminKey = Environment.GetEnvironmentVariable("ADMIN_API_KEY"); // из переменной окружения

// Функция проверки обычного API-ключа
bool IsValidApiKey(string apiKey)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    var cmd = new SqliteCommand("SELECT is_active, expires_at FROM api_keys WHERE key = @key", connection);
    cmd.Parameters.AddWithValue("@key", apiKey);
    using var reader = cmd.ExecuteReader();
    if (reader.Read())
    {
        bool isActive = reader.GetInt32(0) == 1;
        if (!isActive) return false;
        if (!reader.IsDBNull(1))
        {
            DateTime? expiresAt = reader.GetDateTime(1);
            if (expiresAt.HasValue && expiresAt.Value < DateTime.Now) return false;
        }
        return true;
    }
    return false;
}

// Функция проверки мастер-ключа
bool IsAdminKey(string apiKey) => apiKey == adminKey;

// Middleware для проверки ключа (для простоты проверяем в каждом эндпоинте)

// Эндпоинт поиска по коду
app.MapGet("/api/okved/{code}", async (string code, HttpRequest request) =>
{
    if (!request.Headers.TryGetValue("X-API-Key", out var apiKey) || !IsValidApiKey(apiKey!))
        return Results.Unauthorized();

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    var cmd = new SqliteCommand("SELECT name FROM okved2 WHERE code = @code", connection);
    cmd.Parameters.AddWithValue("@code", code);
    var name = await cmd.ExecuteScalarAsync();
    return name != null ? Results.Ok(new { code, name = name.ToString() }) : Results.NotFound();
});

// Эндпоинт поиска по названию
app.MapGet("/api/okved/search", async (string q, HttpRequest request) =>
{
    if (!request.Headers.TryGetValue("X-API-Key", out var apiKey) || !IsValidApiKey(apiKey!))
        return Results.Unauthorized();

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

// Админ-эндпоинт: создание нового ключа
app.MapPost("/admin/create-key", async (HttpRequest request, string clientName, int? daysValid) =>
{
    if (!request.Headers.TryGetValue("X-API-Key", out var apiKey) || !IsAdminKey(apiKey!))
        return Results.Unauthorized();

    string newKey = Guid.NewGuid().ToString();
    DateTime? expiresAt = daysValid.HasValue ? DateTime.Now.AddDays(daysValid.Value) : (DateTime?)null;

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    var cmd = new SqliteCommand("INSERT INTO api_keys (key, client_name, expires_at, is_active) VALUES (@key, @name, @exp, 1)", connection);
    cmd.Parameters.AddWithValue("@key", newKey);
    cmd.Parameters.AddWithValue("@name", clientName);
    cmd.Parameters.AddWithValue("@exp", expiresAt.HasValue ? expiresAt.Value : (object)DBNull.Value);
    try
    {
        await cmd.ExecuteNonQueryAsync();
        return Results.Ok(new { key = newKey, expires = expiresAt });
    }
    catch (SqliteException)
    {
        return Results.BadRequest("Key already exists (unlikely)");
    }
});

// Админ-эндпоинт: обновление справочника (только заглушка, пока ручное)
app.MapPost("/admin/update-dictionary", async (HttpRequest request) =>
{
    if (!request.Headers.TryGetValue("X-API-Key", out var apiKey) || !IsAdminKey(apiKey!))
        return Results.Unauthorized();

    // Здесь позже добавим код для скачивания и парсинга свежего CSV/XML
    return Results.Ok("Update functionality will be implemented soon.");
});

app.Run();
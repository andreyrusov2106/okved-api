using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Text;

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

// Админ-эндпоинт: обновление справочника
app.MapPost("/admin/update-dictionary", async (HttpRequest request) =>
{
    if (!request.Headers.TryGetValue("X-API-Key", out var apiKey) || !IsAdminKey(apiKey!))
        return Results.Unauthorized();

    try
    {
        // 1. Источник данных
        string csvUrl = "https://classifikators.ru/okved?get=kit";

        // 2. Скачиваем файл
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(csvUrl);
        if (!response.IsSuccessStatusCode)
            return Results.BadRequest($"Не удалось скачать CSV. Код: {response.StatusCode}");

        var csvContent = await response.Content.ReadAsStringAsync();

        // 3. Парсим CSV
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();

        // Создаём временную таблицу
        var createTempCmd = new SqliteCommand(@"
            CREATE TEMP TABLE temp_okved2 (
                code TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                name_lower TEXT
            )", connection);
        createTempCmd.Transaction = transaction;
        createTempCmd.ExecuteNonQuery();

        // Разбираем CSV построчно (простой парсинг с учётом кавычек)
        var lines = csvContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        int count = 0;
        // Предполагаем, что первая строка — заголовки, пропускаем её
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = SplitCsvLine(lines[i]);
            if (parts.Length >= 2)
            {
                string code = parts[0].Trim('"');   // код в первой колонке
                string name = parts[1].Trim('"');   // название во второй
                if (string.IsNullOrWhiteSpace(code) || code == " ") continue;

                string nameLower = name.ToLowerInvariant();

                var insertCmd = new SqliteCommand(@"
                    INSERT OR REPLACE INTO temp_okved2 (code, name, name_lower)
                    VALUES (@code, @name, @name_lower)", connection);
                insertCmd.Transaction = transaction;
                insertCmd.Parameters.AddWithValue("@code", code);
                insertCmd.Parameters.AddWithValue("@name", name);
                insertCmd.Parameters.AddWithValue("@name_lower", nameLower);
                insertCmd.ExecuteNonQuery();
                count++;
            }
        }

        if (count == 0) return Results.BadRequest("Не найдено данных в CSV");

        // Атомарная замена таблиц
        var dropMainCmd = new SqliteCommand("DROP TABLE okved2", connection);
        dropMainCmd.Transaction = transaction;
        dropMainCmd.ExecuteNonQuery();

        var renameCmd = new SqliteCommand("ALTER TABLE temp_okved2 RENAME TO okved2", connection);
        renameCmd.Transaction = transaction;
        renameCmd.ExecuteNonQuery();

        transaction.Commit();

        // Опционально: бэкап
        var backupPath = $"okved_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db";
        connection.Close();
        File.Copy("okved.db", backupPath);

        return Results.Ok($"Справочник обновлён. Загружено {count} кодов.");
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Ошибка: {ex.Message}");
    }
});
// <-- ВСПОМОГАТЕЛЬНАЯ ФУНКЦИЯ ДЛЯ РАЗДЕЛЕНИЯ CSV (можно разместить где-то в конце файла) -->
static string[] SplitCsvLine(string line)
{
    var result = new List<string>();
    var inQuotes = false;
    var startIndex = 0;
    for (int i = 0; i < line.Length; i++)
    {
        if (line[i] == '"')
            inQuotes = !inQuotes;
        else if (line[i] == ';' && !inQuotes)
        {
            result.Add(line.Substring(startIndex, i - startIndex));
            startIndex = i + 1;
        }
    }
    result.Add(line.Substring(startIndex));
    return result.ToArray();
}

app.Run();
// ============================================================================
// Exercise 03 — Native AOT Minimal API
// ============================================================================
// Builds a small CRUD-style Minimal API that uses source-generated JSON types
// and an explicit in-memory repository shape that is friendly to Native AOT.
// ============================================================================

using System.Text.Json.Serialization;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Register generated metadata up front so request/response serialization avoids reflection.
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, TodoJsonContext.Default);
});

builder.Services.AddSingleton<TodoRepository>();

WebApplication app = builder.Build();

app.MapGet("/", () => Results.Ok(new AppInfo("Native AOT Minimal API demo", "Use /todos to access the CRUD endpoints.")));

app.MapGet("/todos", (TodoRepository repository) =>
{
    return Results.Ok(repository.GetAll());
});

app.MapGet("/todos/{id:int}", (int id, TodoRepository repository) =>
{
    TodoItem? item = repository.GetById(id);
    return item is null ? Results.NotFound() : Results.Ok(item);
});

app.MapPost("/todos", (CreateTodoRequest request, TodoRepository repository) =>
{
    TodoItem created = repository.Add(request.Title);
    return Results.Created($"/todos/{created.Id}", created);
});

app.MapPut("/todos/{id:int}", (int id, UpdateTodoRequest request, TodoRepository repository) =>
{
    TodoItem? updated = repository.Update(id, request.Title, request.IsCompleted);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

app.MapDelete("/todos/{id:int}", (int id, TodoRepository repository) =>
{
    return repository.Delete(id) ? Results.NoContent() : Results.NotFound();
});

app.Run();

public sealed class TodoRepository
{
    private readonly List<TodoItem> _items =
    [
        new TodoItem(1, "Learn Native AOT basics", false),
        new TodoItem(2, "Replace reflection serializers", true)
    ];

    private int _nextId = 3;

    public IReadOnlyList<TodoItem> GetAll() => _items;

    public TodoItem? GetById(int id) => _items.FirstOrDefault(item => item.Id == id);

    public TodoItem Add(string title)
    {
        TodoItem created = new(_nextId++, title, false);
        _items.Add(created);
        return created;
    }

    public TodoItem? Update(int id, string title, bool isCompleted)
    {
        int index = _items.FindIndex(item => item.Id == id);
        if (index < 0)
        {
            return null;
        }

        // Replace the immutable record with a new value so updates stay explicit and easy to reason about.
        TodoItem updated = new(id, title, isCompleted);
        _items[index] = updated;
        return updated;
    }

    public bool Delete(int id)
    {
        int removed = _items.RemoveAll(item => item.Id == id);
        return removed > 0;
    }
}

public readonly record struct AppInfo(string Name, string Message);
public readonly record struct TodoItem(int Id, string Title, bool IsCompleted);
public readonly record struct CreateTodoRequest(string Title);
public readonly record struct UpdateTodoRequest(string Title, bool IsCompleted);

[JsonSerializable(typeof(AppInfo))]
[JsonSerializable(typeof(TodoItem))]
[JsonSerializable(typeof(List<TodoItem>))]
[JsonSerializable(typeof(CreateTodoRequest))]
[JsonSerializable(typeof(UpdateTodoRequest))]
internal partial class TodoJsonContext : JsonSerializerContext
{
}

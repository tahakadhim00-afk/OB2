using OB2.Core.Organization;
using OB2.Storage.Database;

namespace OB2.Storage.Repositories;

/// <summary>Categories (FR-18) and queues (FR-13, FR-16) over the v2 schema.</summary>
public sealed class SqliteOrganizationRepository : ICategoryRepository, IQueueRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteOrganizationRepository(SqliteConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    IReadOnlyList<Category> ICategoryRepository.List()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, save_folder, extensions FROM categories ORDER BY id";
        using var reader = command.ExecuteReader();
        var categories = new List<Category>();
        while (reader.Read())
        {
            categories.Add(new Category
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                SaveFolder = reader.IsDBNull(2) ? null : reader.GetString(2),
                Extensions = Category.ParseExtensions(reader.IsDBNull(3) ? null : reader.GetString(3)),
            });
        }

        return categories;
    }

    public long Insert(Category category)
    {
        ArgumentNullException.ThrowIfNull(category);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO categories (name, save_folder, extensions) VALUES ($name, $folder, $ext); SELECT last_insert_rowid();";
        BindCategory(command, category);
        category.Id = (long)command.ExecuteScalar()!;
        return category.Id;
    }

    public void Update(Category category)
    {
        ArgumentNullException.ThrowIfNull(category);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE categories SET name = $name, save_folder = $folder, extensions = $ext WHERE id = $id";
        BindCategory(command, category);
        command.Parameters.AddWithValue("$id", category.Id);
        command.ExecuteNonQuery();
    }

    void ICategoryRepository.Delete(long id)
    {
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE downloads SET category_id = NULL WHERE category_id = $id; DELETE FROM categories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    IReadOnlyList<QueueDefinition> IQueueRepository.List()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, concurrency, schedule_json, sort_order FROM queues ORDER BY sort_order, id";
        using var reader = command.ExecuteReader();
        var queues = new List<QueueDefinition>();
        while (reader.Read())
        {
            queues.Add(new QueueDefinition
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Concurrency = reader.GetInt32(2),
                Schedule = QueueSchedule.FromJson(reader.IsDBNull(3) ? null : reader.GetString(3)),
                SortOrder = reader.GetInt32(4),
            });
        }

        return queues;
    }

    public long Insert(QueueDefinition queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO queues (name, concurrency, schedule_json, sort_order) VALUES ($name, $concurrency, $schedule, $order); SELECT last_insert_rowid();";
        BindQueue(command, queue);
        queue.Id = (long)command.ExecuteScalar()!;
        return queue.Id;
    }

    public void Update(QueueDefinition queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE queues SET name = $name, concurrency = $concurrency, schedule_json = $schedule, sort_order = $order WHERE id = $id";
        BindQueue(command, queue);
        command.Parameters.AddWithValue("$id", queue.Id);
        command.ExecuteNonQuery();
    }

    public void Delete(long id, long moveTo)
    {
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE downloads SET queue_id = $to WHERE queue_id = $id; DELETE FROM queues WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$to", moveTo);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void BindCategory(Microsoft.Data.Sqlite.SqliteCommand command, Category category)
    {
        command.Parameters.AddWithValue("$name", category.Name);
        command.Parameters.AddWithValue("$folder", string.IsNullOrWhiteSpace(category.SaveFolder) ? DBNull.Value : category.SaveFolder);
        command.Parameters.AddWithValue("$ext", string.Join(',', category.Extensions));
    }

    private static void BindQueue(Microsoft.Data.Sqlite.SqliteCommand command, QueueDefinition queue)
    {
        command.Parameters.AddWithValue("$name", queue.Name);
        command.Parameters.AddWithValue("$concurrency", Math.Clamp(queue.Concurrency, 1, 32));
        command.Parameters.AddWithValue("$schedule", queue.Schedule.ToJson());
        command.Parameters.AddWithValue("$order", queue.SortOrder);
    }
}

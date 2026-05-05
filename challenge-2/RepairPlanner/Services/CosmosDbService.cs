using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;

namespace RepairPlannerAgent.Services;

public sealed class CosmosDbService(CosmosDbOptions options, ILogger<CosmosDbService> logger)
{
    private readonly CosmosClient _client = new(options.Endpoint, options.Key);
    private readonly Database _database = new CosmosClient(options.Endpoint, options.Key).GetDatabase(options.DatabaseName);

    public async Task<IReadOnlyList<Technician>> GetAvailableTechniciansAsync(IReadOnlyList<string> requiredSkills, CancellationToken ct = default)
    {
        try
        {
            var container = _database.GetContainer("Technicians");
            // Query technicians who have at least one of the required skills and are available
            var query = new QueryDefinition("""
                SELECT * FROM c
                WHERE c.isAvailable = true
                AND EXISTS (SELECT VALUE s FROM s IN c.skills WHERE ARRAY_CONTAINS(@requiredSkills, s))
                """)
                .WithParameter("@requiredSkills", requiredSkills);

            var iterator = container.GetItemQueryIterator<Technician>(query);
            var results = new List<Technician>();

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(ct);
                results.AddRange(response);
            }

            logger.LogInformation("Retrieved {Count} available technicians matching skills: {Skills}", results.Count, string.Join(", ", requiredSkills));
            return results;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error querying available technicians for skills: {Skills}", string.Join(", ", requiredSkills));
            return Array.Empty<Technician>();
        }
    }

    public async Task<IReadOnlyList<Part>> GetPartsByNumbersAsync(IReadOnlyList<string> partNumbers, CancellationToken ct = default)
    {
        try
        {
            var container = _database.GetContainer("PartsInventory");
            var query = new QueryDefinition("SELECT * FROM c WHERE ARRAY_CONTAINS(@partNumbers, c.partNumber)")
                .WithParameter("@partNumbers", partNumbers);

            var iterator = container.GetItemQueryIterator<Part>(query);
            var results = new List<Part>();

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(ct);
                results.AddRange(response);
            }

            logger.LogInformation("Retrieved {Count} parts for numbers: {PartNumbers}", results.Count, string.Join(", ", partNumbers));
            return results;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error querying parts for numbers: {PartNumbers}", string.Join(", ", partNumbers));
            return Array.Empty<Part>();
        }
    }

    public async Task CreateWorkOrderAsync(WorkOrder workOrder, CancellationToken ct = default)
    {
        try
        {
            var container = _database.GetContainer("WorkOrders");
            await container.UpsertItemAsync(workOrder, new PartitionKey(workOrder.Status), cancellationToken: ct);
            logger.LogInformation("Successfully created work order {Id} with status {Status}", workOrder.Id, workOrder.Status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating work order {Id}", workOrder.Id);
            throw;
        }
    }
}
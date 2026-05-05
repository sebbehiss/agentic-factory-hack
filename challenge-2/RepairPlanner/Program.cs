using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;
using RepairPlannerAgent.Services;

var services = new ServiceCollection();

// Configure logging
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

// Configure Cosmos DB options from environment variables
services.AddSingleton(new CosmosDbOptions
{
    Endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? throw new InvalidOperationException("COSMOS_ENDPOINT not set"),
    Key = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? throw new InvalidOperationException("COSMOS_KEY not set"),
    DatabaseName = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME") ?? throw new InvalidOperationException("COSMOS_DATABASE_NAME not set")
});

// Register services
services.AddSingleton<CosmosDbService>();
services.AddSingleton<IFaultMappingService, FaultMappingService>();

// Configure AI Project Client
var endpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT") ?? throw new InvalidOperationException("AZURE_AI_PROJECT_ENDPOINT not set");
var modelDeploymentName = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME") ?? throw new InvalidOperationException("MODEL_DEPLOYMENT_NAME not set");
services.AddSingleton(new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential()));

// Register RepairPlannerAgent
services.AddSingleton(provider => new RepairPlannerAgent.RepairPlannerAgent(
    provider.GetRequiredService<AIProjectClient>(),
    provider.GetRequiredService<CosmosDbService>(),
    provider.GetRequiredService<IFaultMappingService>(),
    modelDeploymentName,
    provider.GetRequiredService<ILogger<RepairPlannerAgent.RepairPlannerAgent>>()
));

var serviceProvider = services.BuildServiceProvider();

await using var scope = serviceProvider.CreateAsyncScope();
var provider = scope.ServiceProvider;

var logger = provider.GetRequiredService<ILogger<Program>>();
var agent = provider.GetRequiredService<RepairPlannerAgent.RepairPlannerAgent>();

try
{
    // Ensure agent is registered
    await agent.EnsureAgentVersionAsync();

    // Create a sample diagnosed fault
    var sampleFault = new DiagnosedFault
    {
        Id = Guid.NewGuid().ToString(),
        MachineId = "TIRE-EXTRUDER-001",
        FaultType = "curing_temperature_excessive",
        Description = "Curing temperature exceeded safe threshold by 50°C",
        Timestamp = DateTimeOffset.UtcNow,
        Severity = "high"
    };

    logger.LogInformation("Processing sample fault: {FaultType} on machine {MachineId}", sampleFault.FaultType, sampleFault.MachineId);

    // Plan and create work order
    var workOrder = await agent.PlanAndCreateWorkOrderAsync(sampleFault);

    logger.LogInformation("Work order created: {WorkOrderNumber} for machine {MachineId}", workOrder.WorkOrderNumber, workOrder.MachineId);
    logger.LogInformation("Assigned to: {AssignedTo}, Priority: {Priority}, Estimated Duration: {Duration} minutes", workOrder.AssignedTo, workOrder.Priority, workOrder.EstimatedDuration);
}
catch (Exception ex)
{
    logger.LogError(ex, "Error in repair planning workflow");
}

logger.LogInformation("Repair planning demonstration completed.");

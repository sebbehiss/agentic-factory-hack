using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;
using RepairPlannerAgent.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepairPlannerAgent;

public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";
    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Generate a repair plan with tasks, timeline, and resource allocation.
        Return the response as valid JSON matching the WorkOrder schema.

        Output JSON with these fields:
        - workOrderNumber, machineId, title, description
        - type: "corrective" | "preventive" | "emergency"
        - priority: "critical" | "high" | "medium" | "low"
        - status, assignedTo (technician id or null), notes
        - estimatedDuration: integer (minutes, e.g. 60 not "60 minutes")
        - partsUsed: [{ partId, partNumber, quantity }]
        - tasks: [{ sequence, title, description, estimatedDurationMinutes (integer), requiredSkills, safetyNotes }]

        IMPORTANT: All duration fields must be integers representing minutes (e.g. 90), not strings.

        Rules:
        - Assign the most qualified available technician
        - Include only relevant parts; empty array if none needed
        - Tasks must be ordered and actionable
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
    {
        var definition = new PromptAgentDefinition(model: modelDeploymentName) { Instructions = AgentInstructions };
        await projectClient.Agents.CreateAgentVersionAsync(AgentName, new AgentVersionCreationOptions(definition), ct);
        logger.LogInformation("Ensured agent version for {AgentName}", AgentName);
    }

    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(DiagnosedFault fault, CancellationToken ct = default)
    {
        // 1. Get required skills and parts from mapping
        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredParts = faultMapping.GetRequiredParts(fault.FaultType);
        logger.LogInformation("Fault {FaultType}: requires skills {Skills}, parts {Parts}",
            fault.FaultType, string.Join(", ", requiredSkills), string.Join(", ", requiredParts));

        // 2. Query technicians and parts from Cosmos DB
        var availableTechnicians = await cosmosDb.GetAvailableTechniciansAsync(requiredSkills, ct);
        var availableParts = await cosmosDb.GetPartsByNumbersAsync(requiredParts, ct);

        // 3. Build prompt and invoke agent
        var prompt = BuildPrompt(fault, availableTechnicians, availableParts);
        var agent = projectClient.GetAIAgent(name: AgentName);
        var response = await agent.RunAsync(prompt, thread: null, options: null, cancellationToken: ct);
        var resultText = response.Text ?? "{}";
        logger.LogInformation("Agent response: {Response}", resultText);

        // 4. Parse response and apply defaults
        var workOrder = JsonSerializer.Deserialize<WorkOrder>(resultText, JsonOptions) ?? new WorkOrder();
        ApplyDefaults(workOrder, fault, availableTechnicians, availableParts);

        // 5. Save to Cosmos DB
        await cosmosDb.CreateWorkOrderAsync(workOrder, ct);

        return workOrder;
    }

    private static string BuildPrompt(DiagnosedFault fault, IReadOnlyList<Technician> technicians, IReadOnlyList<Part> parts)
    {
        var techniciansJson = JsonSerializer.Serialize(technicians);
        var partsJson = JsonSerializer.Serialize(parts);
        return $"""
            Diagnosed Fault:
            - Machine ID: {fault.MachineId}
            - Fault Type: {fault.FaultType}
            - Description: {fault.Description}
            - Severity: {fault.Severity}

            Available Technicians:
            {techniciansJson}

            Available Parts:
            {partsJson}

            Generate a repair work order in JSON format.
            """;
    }

    private static void ApplyDefaults(WorkOrder workOrder, DiagnosedFault fault, IReadOnlyList<Technician> technicians, IReadOnlyList<Part> parts)
    {
        workOrder.Id ??= Guid.NewGuid().ToString();
        workOrder.WorkOrderNumber ??= $"WO-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8)}";
        workOrder.MachineId ??= fault.MachineId;
        workOrder.Status ??= "open";
        workOrder.Type ??= "corrective";
        workOrder.Priority ??= "medium";
        workOrder.EstimatedDuration = workOrder.EstimatedDuration == 0 ? 60 : workOrder.EstimatedDuration;

        // Assign first available technician if not assigned
        if (string.IsNullOrEmpty(workOrder.AssignedTo) && technicians.Count > 0)
        {
            workOrder.AssignedTo = technicians[0].Id;
        }

        // Ensure partsUsed have ids
        foreach (var partUsage in workOrder.PartsUsed)
        {
            if (string.IsNullOrEmpty(partUsage.PartId))
            {
                var part = parts.FirstOrDefault(p => p.PartNumber == partUsage.PartNumber);
                if (part != null)
                {
                    partUsage.PartId = part.Id;
                }
            }
        }
    }
}
using System.Text.Json;

namespace AzureArchive.VideoTools.Tests;

internal static class SyntheticAap
{
    public const string ScriptNodeGuid = "6fcb9e8d-a1e2-4bb4-a610-b59795bd1036";
    public const string SecondScriptNodeGuid = "eef201c6-7ad3-4018-929c-f2df53603c71";

    public static string Scene(
        string text,
        string? sourceType = "ScriptData, Assembly-CSharp",
        bool reversedPropertyOrder = false,
        string additionalPrompt = "")
    {
        string encodedText = JsonSerializer.Serialize(text);
        string encodedType = JsonSerializer.Serialize(sourceType ?? string.Empty);
        string encodedAdditionalPrompt = JsonSerializer.Serialize(additionalPrompt);
        if (reversedPropertyOrder)
        {
            return $$"""
            {
              "voice": "",
              "sound": "",
              "bgFriendlyName": "BG_Test",
              "placeText": "",
              "additionalPrompt": {{encodedAdditionalPrompt}},
              "isDialogScript": true,
              "text": {{encodedText}},
              "$type": {{encodedType}}
            }
            """;
        }

        return $$"""
        {"$type":{{encodedType}},"text":{{encodedText}},"isDialogScript":true,"additionalPrompt":{{encodedAdditionalPrompt}},"placeText":"","bgFriendlyName":"BG_Test","sound":"","voice":""}
        """;
    }

    public static string SceneWithCharacterTransition(
        string text,
        int physicalSlot,
        int startingPosition,
        int endingPosition)
    {
        Dictionary<string, object>[] characters = Enumerable.Range(0, 6)
            .Select(slot => new Dictionary<string, object>
            {
                ["name"] = slot == physicalSlot ? "test-character" : string.Empty,
                ["faceId"] = slot == physicalSlot ? "03" : "00",
                ["startingPos"] = slot == physicalSlot ? startingPosition : 0,
                ["endingPos"] = slot == physicalSlot ? endingPosition : 0,
                ["emoticon"] = slot == physicalSlot ? 2 : -1,
                ["action"] = slot == physicalSlot ? 4 : 0,
                ["effect"] = slot == physicalSlot ? 1 : 0,
                ["appear"] = 0,
                ["shapeOverride"] = slot == physicalSlot ? 1 : 0
            })
            .ToArray();

        var scene = new Dictionary<string, object>
        {
            ["$type"] = "ScriptData, Assembly-CSharp",
            ["text"] = text,
            ["isDialogScript"] = true,
            ["additionalPrompt"] = string.Empty,
            ["placeText"] = string.Empty,
            ["bgFriendlyName"] = "BG_Test",
            ["sound"] = string.Empty,
            ["voice"] = string.Empty,
            ["speakerSlotNum"] = physicalSlot,
            ["highlightedSlotNums"] = new Dictionary<string, object>
            {
                ["$values"] = new[] { 1, physicalSlot }
            },
            ["characters"] = new Dictionary<string, object>
            {
                ["$values"] = characters
            }
        };
        return JsonSerializer.Serialize(scene);
    }

    public static string Project(
        IReadOnlyList<string> scenes,
        string rootType = "ProjectData, Assembly-CSharp",
        string scriptNodeGuid = ScriptNodeGuid)
    {
        string scripts = string.Join(",", scenes);
        return $$"""
        {
          "$type": {{JsonSerializer.Serialize(rootType)}},
          "ProjectName": "Synthetic Project",
          "PreviewBgName": 123,
          "PreviewHeader": "Header",
          "PreviewTitle": "Title",
          "nodes": {
            "$type": "System.Collections.Generic.List`1[[NodeData, Assembly-CSharp]], mscorlib",
            "$values": [
              {
                "$type": "EntryNodeData, Assembly-CSharp",
                "Title": "Entry",
                "Header": "Header",
                "Guid": "00000000-0000-0000-0000-000000000000",
                "ConnectionsTo": {"$values":[{{JsonSerializer.Serialize(scriptNodeGuid)}}]},
                "X": 0,
                "Y": 0
              },
              {
                "$type": "ScriptNodeData, Assembly-CSharp",
                "Scripts": {
                  "$type": "System.Collections.Generic.List`1[[ScriptData, Assembly-CSharp]], mscorlib",
                  "$values": [{{scripts}}]
                },
                "NodeName": "Main",
                "Guid": {{JsonSerializer.Serialize(scriptNodeGuid)}},
                "ConnectionsTo": {"$values":["ad9b3be5-fe7f-4581-aa7d-9f7774248d18"]},
                "X": 0,
                "Y": -100
              },
              {
                "$type": "ExitNodeData, Assembly-CSharp",
                "Guid": "ad9b3be5-fe7f-4581-aa7d-9f7774248d18",
                "ConnectionsTo": {"$values":[]},
                "X": 0,
                "Y": -200
              }
            ]
          }
        }
        """;
    }

    public static string ProjectWithTwoScriptNodes(
        IReadOnlyList<string> firstNodeScenes,
        IReadOnlyList<string> secondNodeScenes)
    {
        string firstScripts = string.Join(",", firstNodeScenes);
        string secondScripts = string.Join(",", secondNodeScenes);
        return $$"""
        {
          "$type": "ProjectData, Assembly-CSharp",
          "ProjectName": "Synthetic Project",
          "PreviewBgName": 123,
          "nodes": {
            "$values": [
              {
                "$type": "EntryNodeData, Assembly-CSharp",
                "Guid": "00000000-0000-0000-0000-000000000000",
                "ConnectionsTo": {"$values":["{{ScriptNodeGuid}}"]}
              },
              {
                "$type": "ScriptNodeData, Assembly-CSharp",
                "Scripts": {"$values":[{{firstScripts}}]},
                "Guid": "{{ScriptNodeGuid}}",
                "ConnectionsTo": {"$values":["{{SecondScriptNodeGuid}}"]}
              },
              {
                "$type": "ScriptNodeData, Assembly-CSharp",
                "Scripts": {"$values":[{{secondScripts}}]},
                "Guid": "{{SecondScriptNodeGuid}}",
                "ConnectionsTo": {"$values":["ad9b3be5-fe7f-4581-aa7d-9f7774248d18"]}
              },
              {
                "$type": "ExitNodeData, Assembly-CSharp",
                "Guid": "ad9b3be5-fe7f-4581-aa7d-9f7774248d18",
                "ConnectionsTo": {"$values":[]}
              }
            ]
          }
        }
        """;
    }
}

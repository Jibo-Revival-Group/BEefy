using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

/// <summary>Deterministic everyday HA commands; values are validated again by HA.</summary>
public static class HomeAssistantEntityCommandParser
{
    public sealed record EntityCommand(string Domain, string Action, string Scope, string? TargetName,
        string? Value = null, string? Unit = null)
    {
        public IReadOnlyDictionary<string, string> Parameters(string? entityId = null)
        {
            var data = new Dictionary<string, string> { ["domain"] = Domain, ["action"] = Action,
                ["scope"] = entityId is null ? Scope : "entity" };
            if (TargetName is not null) data["targetName"] = TargetName;
            if (Value is not null) data["value"] = Value;
            if (Unit is not null) data["unit"] = Unit;
            if (entityId is not null) data["entityId"] = entityId;
            return data;
        }
    }

    private const string Devices = @"lights?|lamps?|thermostats?|switch(?:es)?|fans?|covers?|blinds?|shades?|curtains?";
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryParse(string? transcript, out EntityCommand command)
    {
        command = null!;
        var text = Regex.Replace((transcript ?? "").Trim().ToLowerInvariant(), @"[^\w.%-]+", " ").TrimEnd('.', ' ');
        text = TranscriptTextNormalizer.StripLeadingPhrases(text,
            ["hey jibo", "jibo", "please", "can you", "could you"]);
        text = Regex.Replace(text, @"(?:\s+(?:please|thanks|thank you))+$", "");
        if (Regex.IsMatch(text, @"\bhue\b")) return false;
        var activate = Match(text, @"^(?:activate|run|turn on) (?:the )?(?<kind>scene|script) (?<name>.+)$");
        if (!activate.Success)
            activate = Match(text, @"^(?:activate|run) (?:the )?(?<name>.+?) (?<kind>scene|script)$");
        if (activate.Success)
        {
            command = new(activate.Groups["kind"].Value, "activate", "named", activate.Groups["name"].Value);
            return true;
        }
        var m = Match(text, @"^(?:turn|switch) (?<action>on|off) (?:the )?(?<target>.+)$");
        if (!m.Success) m = Match(text, @"^(?:turn|switch) (?:the )?(?<target>.+?) (?<action>on|off)$");
        if (!m.Success) m = Match(text, @"^(?<target>all (?:the )?(?:" + Devices + @")) (?<action>on|off)$");
        string? value = null;
        string? unit = null;
        var action = "";
        if (m.Success) action = "turn_" + m.Groups["action"].Value;
        else
        {
            m = Match(text, @"^(?<action>open|close|stop) (?:the )?(?<target>.+)$");
            if (m.Success) action = m.Groups["action"].Value;
            else
            {
                m = Match(text, @"^(?:set|make|dim) (?:the )?(?<target>.+?) (?:to )?(?<value>-?\d+(?:\.\d+)?)(?:\s*(?<unit>percent|%|kelvin|degrees(?: (?:celsius|fahrenheit))?|celsius|fahrenheit))?$");
                if (!m.Success)
                    m = Match(text, @"^(?:set|make) (?:the )?(?<target>.+?) (?:to )?(?<value>warm white|cool white|red|green|blue|yellow|orange|purple|pink|white|cyan|magenta|heat cool|heat|cool|auto|off|dry|fan only)$");
                if (!m.Success) return false;
                value = m.Groups["value"].Value;
                unit = m.Groups["unit"].Value;
            }
        }
        var target = Regex.Replace(m.Groups["target"].Value,
            @"\s+(?:color temperature|color|brightness|temperature|hvac mode|mode|speed|position)$", "");
        var device = Match(target, @"\b(?<device>" + Devices + @")\b");
        if (!device.Success) return false; // Do not claim arbitrary conversation as a device command.
        var noun = device.Groups["device"].Value;
        var domain = noun.StartsWith("light") || noun.StartsWith("lamp") ? "light" :
            noun.StartsWith("thermostat") ? "climate" : noun.StartsWith("switch") ? "switch" :
            noun.StartsWith("fan") ? "fan" : "cover";
        if (action == "" && value is not null)
        {
            action = domain switch
            {
                "light" when unit == "kelvin" || value is "warm white" or "cool white" => "color_temperature",
                "light" when decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out _) => "brightness",
                "light" => "color",
                "fan" => "percentage",
                "cover" => "position",
                "climate" when decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out _) => "temperature",
                "climate" => "hvac_mode",
                _ => ""
            };
        }
        if (action == "") return false;
        if (domain == "climate" && action == "turn_off") { action = "hvac_mode"; value = "off"; }
        if (domain == "climate" && action == "turn_on") return false;
        if ((action is "open" or "close" or "stop") && domain != "cover") return false;
        // Keep existing room/named light commands and unitless thermostat commands compatible.
        var name = Regex.Replace(target, @"\b(?:the|" + Devices + @")\b", "");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        var scope = "named";
        if (name is "" or "here" or "in here" or "in this room" or "in my room" or "in this area")
            { name = ""; scope = "room"; }
        else if (name is "all" or "all the" or "whole house" or "entire house")
            { name = ""; scope = "all"; }
        else if (name.StartsWith("in ")) { name = name[3..].Trim(); scope = "area"; }
        else if (noun.EndsWith('s')) scope = "area";
        if (domain == "light" && (action is "turn_on" or "turn_off") && (scope is "room" or "named")) return false;
        if (domain == "climate" && action == "temperature" && (scope is "room" or "named") &&
            !(unit ?? "").Contains("celsius") && !(unit ?? "").Contains("fahrenheit")) return false;
        command = new(domain, action, scope, name.Length == 0 ? null : name, value, unit);
        return true;
    }
}

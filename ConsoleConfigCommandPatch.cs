using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace RedAllianceSpeedrun
{
    // In-game console support: type `racfg <subcommand>` in the developer console to
    // inspect / edit any of the mod's ConfigEntry values at runtime. Saved to disk on
    // `racfg save`; static field cache (Plugin internal statics) is re-synced after each
    // set so patches that read them see new values immediately.
    //
    // Subcommands:
    //   racfg help                       — usage
    //   racfg list                       — list all keys with current values
    //   racfg list <section>             — list keys in one section
    //   racfg get <key>                  — show single value (key OR section.key)
    //   racfg set <key> <value>          — set value (key OR section.key, lower or original case)
    //   racfg save                       — flush to BepInEx/config/redalliance.speedrun.v2.cfg
    //   racfg reload                     — re-read disk file
    [HarmonyPatch(typeof(DeveloperConsoleScript), "ReadCommand")]
    internal static class ConsoleConfigCommandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(string command)
        {
            if (string.IsNullOrEmpty(command)) return true;
            string trimmed = command.TrimStart();
            if (!trimmed.StartsWith("racfg", StringComparison.OrdinalIgnoreCase)) return true;

            string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                PrintHelp();
                return false;
            }

            string sub = parts[1].ToLowerInvariant();
            try
            {
                switch (sub)
                {
                    case "help": PrintHelp(); break;
                    case "list":
                        if (parts.Length >= 3) ListSection(parts[2]);
                        else ListAll();
                        break;
                    case "get":
                        if (parts.Length < 3) { Msg("Usage: racfg get <key>"); break; }
                        Get(parts[2]);
                        break;
                    case "set":
                        if (parts.Length < 4) { Msg("Usage: racfg set <key> <value>"); break; }
                        Set(parts[2], string.Join(" ", parts, 3, parts.Length - 3));
                        break;
                    case "save":
                        Plugin.ConfigRef.Save();
                        Msg("Config flushed to disk.");
                        break;
                    case "reload":
                        Plugin.ConfigRef.Reload();
                        Plugin.Instance?.SyncStatics();
                        Msg("Config reloaded from disk.");
                        break;
                    default:
                        Msg("Unknown subcommand '" + sub + "'. Use 'racfg help'.");
                        break;
                }
            }
            catch (Exception e)
            {
                Msg("Error: " + e.Message);
            }

            return false; // skip original ReadCommand processing
        }

        private static void Msg(string text)
        {
            DeveloperConsoleScript.AddConsoleMessage("[racfg] " + text);
        }

        private static void PrintHelp()
        {
            Msg("racfg help                  — this message");
            Msg("racfg list [section]        — list keys (all, or filtered to section)");
            Msg("racfg get <key>             — show value of <key> or <section.key>");
            Msg("racfg set <key> <value>     — set value (auto-applied to running patches)");
            Msg("racfg save                  — flush changes to .cfg file");
            Msg("racfg reload                — re-read .cfg file from disk");
        }

        private static IEnumerable<KeyValuePair<ConfigDefinition, ConfigEntryBase>> AllEntries()
        {
            if (Plugin.ConfigRef == null) yield break;
            foreach (var def in Plugin.ConfigRef.Keys)
            {
                yield return new KeyValuePair<ConfigDefinition, ConfigEntryBase>(def, Plugin.ConfigRef[def]);
            }
        }

        private static void ListAll()
        {
            int count = 0;
            foreach (var kvp in AllEntries())
            {
                Msg($"{kvp.Key.Section}.{kvp.Key.Key} = {Stringify(kvp.Value.BoxedValue)}");
                count++;
            }
            if (count == 0) Msg("(no entries)");
        }

        private static void ListSection(string section)
        {
            int count = 0;
            foreach (var kvp in AllEntries())
            {
                if (!string.Equals(kvp.Key.Section, section, StringComparison.OrdinalIgnoreCase)) continue;
                Msg($"{kvp.Key.Section}.{kvp.Key.Key} = {Stringify(kvp.Value.BoxedValue)}");
                count++;
            }
            if (count == 0) Msg($"(no entries in section '{section}')");
        }

        private static ConfigEntryBase FindEntry(string query)
        {
            // Try section.key
            string sec = null, key = query;
            int dot = query.IndexOf('.');
            if (dot >= 0)
            {
                sec = query.Substring(0, dot);
                key = query.Substring(dot + 1);
            }

            ConfigEntryBase match = null;
            int matches = 0;
            foreach (var kvp in AllEntries())
            {
                bool keyMatch = string.Equals(kvp.Key.Key, key, StringComparison.OrdinalIgnoreCase);
                if (!keyMatch) continue;
                if (sec != null && !string.Equals(kvp.Key.Section, sec, StringComparison.OrdinalIgnoreCase)) continue;
                match = kvp.Value;
                matches++;
            }
            if (matches > 1)
            {
                Msg($"Ambiguous: '{query}' matches {matches} entries. Use 'section.key'.");
                return null;
            }
            return match;
        }

        private static void Get(string query)
        {
            var e = FindEntry(query);
            if (e == null) { Msg($"Key not found: {query}"); return; }
            Msg($"{e.Definition.Section}.{e.Definition.Key} = {Stringify(e.BoxedValue)}");
        }

        private static void Set(string query, string value)
        {
            var e = FindEntry(query);
            if (e == null) { Msg($"Key not found: {query}"); return; }
            try
            {
                object parsed = TomlTypeConverter.ConvertToValue(value, e.SettingType);
                e.BoxedValue = parsed;
                Plugin.Instance?.SyncStatics();
                Msg($"{e.Definition.Section}.{e.Definition.Key} = {Stringify(e.BoxedValue)}");
            }
            catch (Exception ex)
            {
                Msg($"Parse failed (type {e.SettingType.Name}): {ex.Message}");
            }
        }

        private static string Stringify(object v)
        {
            if (v == null) return "null";
            if (v is string s) return "\"" + s + "\"";
            return v.ToString();
        }
    }
}

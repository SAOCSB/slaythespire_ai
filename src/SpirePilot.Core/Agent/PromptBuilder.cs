using System.Text;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Route;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Agent;

/// <summary>Builds the system prompt (stable, cacheable) and the per-decision user message.</summary>
public static class PromptBuilder
{
    /// <summary>
    /// The system prompt only changes when the user edits standing settings, so providers with prompt caching
    /// reuse it across every decision of a run.
    /// </summary>
    public static string BuildSystem(PilotSettings settings, string messageLanguage)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Persona);
        sb.AppendLine();
        sb.AppendLine(OutputRules);
        sb.AppendLine();
        sb.AppendLine(TeamRules.Replace("{LANG}", messageLanguage));
        sb.AppendLine();
        sb.AppendLine(GameKnowledge);

        var standing = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.Strategy.Instruction))
            standing.Append("- Play style: ").AppendLine(settings.Strategy.Instruction.Trim());
        standing.Append("- Route preference: ").Append(DescribePreset(settings.Route.Preset)).AppendLine();
        if (!string.IsNullOrWhiteSpace(settings.Route.Instruction))
            standing.Append("- Route instruction: ").AppendLine(settings.Route.Instruction.Trim());
        if (settings.Characters.Allowed.Count > 0)
            standing.Append("- Allowed characters when starting a run: ").AppendLine(string.Join(", ", settings.Characters.Allowed));
        if (!settings.Team.AllowGivingPotions)
            standing.AppendLine("- Do NOT use your potions on teammates.");
        if (!settings.Team.ProactiveRequests)
            standing.AppendLine("- Do not start requests to teammates yourself; only answer them.");
        if (!settings.Team.AutoReply)
            standing.AppendLine("- Do not send team messages at all (messages: []).");
        sb.AppendLine();
        sb.AppendLine("## Standing instructions from your human (always follow)");
        sb.Append(standing);
        return sb.ToString();
    }

    public static string BuildUser(GameSnapshot snapshot, DecisionContext context, StateFormatter formatter)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Current state");
        sb.Append(formatter.Format(snapshot));

        if (context.RouteSuggestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Route planner (best paths from here; M=monster E=elite R=rest $=shop ?=event T=treasure)");
            for (int i = 0; i < context.RouteSuggestions.Count; i++)
            {
                var p = context.RouteSuggestions[i];
                sb.Append("  ").Append(i + 1).Append(". ").Append(string.Join(">", p.Nodes)).Append("  [").Append(p.Summary).Append("] score ").Append(p.Score).AppendLine();
            }
            if (context.PinnedNext != null) sb.Append("  The human pinned a route; its next node is ").Append(context.PinnedNext).AppendLine(".");
        }

        if (context.Inbox.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## New team messages (answer requests addressed to you; reply_to = msg id)");
            foreach (var m in context.Inbox)
            {
                sb.Append("  [").Append(m.Id).Append("] ").Append(m.FromName).Append(m.FromAi ? " (AI)" : "").Append(" → ")
                  .Append(m.To == 0 ? "everyone" : "you").Append(" | ").Append(DecisionParser.KindToWire(m.Kind));
                if (m.Potion != null) sb.Append(" potion=").Append(m.Potion);
                if (m.Debuff != null) sb.Append(" debuff=").Append(m.Debuff);
                if (m.Target != null) sb.Append(" target=").Append(m.Target);
                if (m.ReplyTo != null) sb.Append(" reply_to=").Append(m.ReplyTo);
                sb.Append(": ").AppendLine(m.Text);
            }
        }
        if (context.OpenRequests.Count > 0)
        {
            sb.AppendLine("## Team requests you accepted earlier and may still fulfil");
            foreach (var m in context.OpenRequests)
                sb.Append("  [").Append(m.Id).Append("] ").Append(m.FromName).Append(": ").Append(DecisionParser.KindToWire(m.Kind)).Append(" ").AppendLine(m.Text);
        }

        if (context.History.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Your recent actions (oldest first)");
            foreach (var h in context.History) sb.Append("  - ").AppendLine(h);
        }
        if (context.Memos.Count > 0)
        {
            sb.AppendLine("## Your memos");
            foreach (var m in context.Memos) sb.Append("  - ").AppendLine(m);
        }
        if (!string.IsNullOrWhiteSpace(context.LiveInstruction))
        {
            sb.AppendLine();
            sb.AppendLine("## Instruction from your human right now (highest priority)");
            sb.AppendLine(context.LiveInstruction.Trim());
        }
        if (!string.IsNullOrWhiteSpace(context.Feedback))
        {
            sb.AppendLine();
            sb.Append("## Problem with your previous answer: ").AppendLine(context.Feedback);
        }

        sb.AppendLine();
        sb.AppendLine("## Legal actions (answer with these ids)");
        foreach (var a in snapshot.Actions) sb.Append("  ").Append(a.Id).Append(": ").AppendLine(a.Label);
        sb.AppendLine();
        sb.Append("Reply with one JSON object only.");
        if (snapshot.Screen == ScreenKind.Combat)
            sb.Append(" In combat you may list several ids to plan this turn (they run in order; the plan stops if anything changes).");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string DescribePreset(RoutePreset preset) => preset switch
    {
        RoutePreset.Aggressive => "aggressive — take elites for relics when HP allows",
        RoutePreset.Safe => "safe — avoid elites, value rest sites",
        RoutePreset.Shopper => "shops — visit shops, spend gold on removals and key cards",
        RoutePreset.Explorer => "events — prefer unknown rooms",
        RoutePreset.Custom => "custom weights (see route planner scores)",
        _ => "balanced",
    };

    private const string Persona = """
    You are SpirePilot, an expert Slay the Spire 2 player controlling one character in the game through a mod.
    Each turn you receive the current game state and a numbered list of legal actions, and you choose what to do.
    You play to win the run: survive, build a coherent deck, and beat the act bosses. In co-op you are a good teammate.
    """;

    private const string OutputRules = """
    ## Answer format
    Reply with exactly one JSON object and nothing else:
    {
      "thought": "one or two short sentences of reasoning",
      "actions": ["a3"],
      "messages": [],
      "memo": ""
    }
    - "actions": ids copied from the legal action list. The first id is executed now. In combat you may append more
      ids to plan the rest of your turn in order (e.g. ["a2","a0","a5"]); each later step only runs if it is still
      legal after the previous one resolves, so plan only what does not depend on unknown draws or random effects.
      Never invent ids. If unsure, choose a single action.
    - "messages": optional team messages (co-op only), see below. Use [] when there is nothing worth saying.
    - "memo": optional short note to remember for later decisions (deck plan, threats, promises to teammates).
    - Card and enemy names are shown in the game's language; ids like h2 (hand slot), e0 (enemy), p1 (player) are references.
    """;

    private const string TeamRules = """
    ## Team play (co-op)
    Teammates and their potions, powers and HP are listed in the state. Messages you send appear in their game.
    Message object: {"to": "all" | teammate name, "type": one of chat, request_potion, offer_potion, ask_need_potion,
    request_debuff, request_focus, request_block, suggest_route, accept, decline, "text": short sentence in {LANG},
    optional "potion", "debuff", "target", "reply_to": id of the message you answer}.
    - Answer every request addressed to you with accept or decline (reply_to set), then act on what you accepted.
    - To give a potion: use a potion that can target a player (target type AnyPlayer/AnyAlly) on that teammate —
      the legal action list shows "potion → teammate" options. Potions cannot be handed over otherwise.
    - To fulfil a debuff request: play cards that apply that debuff to the named enemy this turn if you reasonably can.
    - Ask for help when it matters (low HP before a big attack, a teammate holds a potion you need, an enemy should be
      debuffed before your big attack). Offer potions when a teammate is in danger.
    - Be brief, at most one or two messages per decision, and never spam. Do not repeat a request that is still open.
    """;

    private const string GameKnowledge = """
    ## Strategy reminders
    - Combat: read enemy intents. Block what you must, kill what you can; killing an attacker removes its damage.
      Spend energy efficiently; play powers early in long fights; avoid taking unblocked damage you can prevent cheaply.
      Use potions when they prevent big damage or win elite/boss fights — do not hoard them until death.
      Apply Vulnerable before big attacks; Weak reduces enemy attack damage.
    - Deck building: take cards that fit a plan; skipping a card reward is often right. Remove Strikes/Defends and curses
      when you can. Upgrade key cards at rest sites unless HP is low.
    - Pathing: elites give relics but are dangerous early or at low HP; rest before the boss; shops are valuable with
      150+ gold. Follow the human's route preferences.
    - Events: prefer options with clear upside; avoid losing max HP or gaining curses unless the reward is great.
    - Co-op: coordinate debuffs and focus fire, protect the weakest teammate, share potions sensibly.
    """;
}

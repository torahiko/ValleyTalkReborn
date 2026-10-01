# ValleyTalk Reborn

<p align="center">
  <img src="https://img.shields.io/badge/Stardew%20Valley-1.6%2B-brightgreen.svg" alt="Stardew Valley 1.6+">
  <img src="https://img.shields.io/badge/SMAPI-4.1.0%2B-blue.svg" alt="SMAPI 4.1.0+">
  <img src="https://img.shields.io/badge/.NET-6.0-purple.svg" alt=".NET 6.0">
  <img src="https://img.shields.io/badge/License-GPL%20v3-orange.svg" alt="License GPL v3">
  <img src="https://img.shields.io/badge/Nexus%20Mods-30319-yellow.svg" alt="Nexus Mods 30319">
</p>

<p align="center">
  <strong>Infinite, Living AI Dialogue & Social Immersion Engine for Stardew Valley</strong>
</p>

<p align="center">
  ValleyTalk Reborn brings Pelican Town to life by connecting Stardew Valley NPCs to modern Large Language Models (LLMs). Experience truly emergent, unbounded dialogue tailored to your farmer's actions, seasons, weather, relationship milestones, emotional nuances, and shared town history—all powered by an advanced streaming interface and local/cloud AI providers.
</p>

---

## 🌟 Key Highlights

- 🗣️ **Infinite Dynamic Dialogue**: Talk freely with any villager without canned dialogue or repetitive loops.
- ⚡ **Real-Time Streaming UI**: Smooth, word-by-word streaming text with natural punctuation-based rhythmic pauses.
- 💬 **Suggested Choices & Free-Form Typing**: Choose from AI-generated contextual replies or type your own custom response anytime (`Left Alt`).
- 👁️ **World & Player Perception Engine**: Villagers actively notice when you fish, forage, chop trees, eat, place objects, gift others, or dig through trash cans.
- 🚶 **Companion & Outing System**: Invite villagers to walk with you (`G`), explore date spots, or enjoy autonomous spouse schedules.
- 🗨️ **Ambient Barks & NPC-to-NPC (A2A) Banter**: Hear villagers murmur contextual barks as you pass by, or listen in as two NPCs converse when meeting on town paths.
- 💖 **Dynamic Emotion & Mood Shocks**: Multi-dimensional emotional states (Valence, Arousal, Openness) that respond to dialogue, gifts, and daily surprises.
- 📖 **Hierarchical Memory & Chronicles**: Automated daily journals, weekly recaps, seasonal milestones, and commitment/promise tracking (`J`).
- 🎨 **Character Studio & AI Bio Wizard**: In-game visual editor (`K`) to inspect and edit character personas, behavioral guidelines, and affection ladders.
- 🌐 **Broad LLM Provider Support**: Native integrations with OpenAI, Anthropic Claude, Google Gemini, DeepSeek, Grok, Mistral, VolcEngine (Doubao), Ollama, LM Studio, and generic OpenAI-compatible endpoints (OpenRouter, SiliconFlow, vLLM, etc.).

---

## 📖 Table of Contents

- [Core Features](#-core-features)
- [Supported AI Providers](#-supported-ai-providers)
- [Requirements](#-requirements)
- [Installation](#-installation)
- [Default Controls & Hotkeys](#-default-controls--hotkeys)
- [Configuration Guide](#-configuration-guide)
- [In-Game Management Hub & Chronicle](#-in-game-management-hub--chronicle)
- [Console Commands](#-console-commands)
- [Authoring Custom NPC Bios](#-authoring-custom-npc-bios)
- [Compatibility & Extensibility](#-compatibility--extensibility)
- [FAQ & Troubleshooting](#-faq--troubleshooting)
- [License & Acknowledgments](#-license--acknowledgments)

---

## 🎯 Core Features

### 1. Context-Aware Infinite Conversations
Every conversation dynamically synthesizes:
- **Game State**: Year, season, weekday, exact time, weather, active festivals, and community center progress.
- **Relationship Dynamics**: Heart levels, marriage/roommate status, jealousy checks, gift streak tracking, and previous conversations.
- **Surroundings & Location**: Current map, nearby landmarks (Points of Interest), indoor/outdoor micro-environments.

### 2. Streaming Dialogue Box with Rhythmic Typing
- **Fluid Word-by-Word Streaming**: Responses stream from the LLM in real time to avoid long pauses.
- **Rhythmic Typing Cadence**: Mimics authentic speech pauses after commas, periods, question marks, and line breaks.
- **Portraits & Expression Sync**: Dynamic portrait expressions react to the emotional subtext of the conversation.

### 3. Interactive Dialogue Modes
- **Suggested Choices**: The AI drafts 2–3 contextual responses based on the villager's prompt.
- **Free-Form Player Input**: Press `Left Alt` (or click the custom input icon) to type whatever you want to say in your own words.
- **Display Flexibility**: Switch between vanilla-style inline dialogue options and custom floating selection cards.

### 4. Reactive Perception Engine
Pelican Town residents are no longer oblivious to your presence. NPCs perceive:
- **Direct Actions**: Eating food, fishing, harvesting crops, tree chopping, and object placement.
- **Social Situations**: Giving gifts to other villagers nearby (or your spouse), talking with neighbors, or digging through garbage cans.
- **Spatial Awareness**: Multi-tier detection ranges (Nearby, Same Map, and Global incidents).

### 5. Companions, Dates & Spouse Freedom
- **Recruit Companions**: Approach any eligible friend and press `G` to invite them along.
- **Smart Pathfinding & Departure**: Companions intelligently follow across maps and take logical departure routes when dismissed.
- **Spouse Autonomy**: Spouses no longer spend entire days pacing the farmhouse; they venture outdoors, maintain hobbies, and visit town on realistic routines.
- **Dates & Outings** *(Optional)*: Embark on planned dates at romantic spots throughout the valley.

### 6. Ambient Barks & NPC-to-NPC (A2A) Interaction
- **Ambient Barks**: Passing near a villager may prompt a spontaneous contextual thought bubble based on their current activity and weather.
- **Agent-to-Agent (A2A)**: When two NPCs cross paths, they can stop and exchange greetings or share rumors about recent town happenings.

### 7. Emotional Models & Mood Shocks
- **3D Emotion Vector**: Models each character along **Valence** (positive/negative), **Arousal** (calm/excited), and **Openness** (reserved/expressive).
- **Mood Shocks**: Major events, thoughtful gifts, or awkward interactions apply temporary emotional shocks that naturally decay over in-game hours.
- **Today's Scene**: Villagers wake up with daily scene context (e.g., feeling energized, preoccupied with chores, or nostalgic).

### 8. Hierarchical Memory & Timeline Chronicles
- **Short-Term Context**: Retains recent turns to maintain coherent back-and-forth dialogue.
- **Multi-Tier Distillation**: Automatically synthesizes daily diaries, weekly highlights, seasonal recaps, and annual town records.
- **Promise & Milestone Tracking**: Tracks promises made between you and villagers (e.g., meeting up later or bringing a specific item).

---

## 🤖 Supported AI Providers

ValleyTalk Reborn features an independent profile architecture—each provider stores its own server address, model name, and API key.

| Provider | Type | Recommended Models | Description |
| :--- | :--- | :--- | :--- |
| **OpenAI** | Cloud | `gpt-4o`, `gpt-4o-mini` | Official OpenAI API. High response speed and roleplay quality. |
| **Anthropic Claude** | Cloud | `claude-3-5-sonnet-latest`, `claude-3-haiku` | Exceptional character nuance and creative writing depth. |
| **Google Gemini** | Cloud | `gemini-1.5-flash`, `gemini-1.5-pro` | Fast throughput and cost-effective performance. |
| **DeepSeek** | Cloud | `deepseek-chat` (V3), `deepseek-reasoner` (R1) | High intelligence-to-cost ratio and bilingual fluency. |
| **xAI Grok** | Cloud | `grok-beta`, `grok-2` | Native xAI Grok provider integration. |
| **Mistral AI** | Cloud | `mistral-large-latest`, `mistral-small` | Official Mistral platform endpoints. |
| **VolcEngine (Doubao)** | Cloud | `doubao-pro-32k`, `doubao-lite-32k` | ByteDance Volcano Engine platform models. |
| **Ollama** | Local / Offline | `qwen2.5:7b`, `llama3.1:8b`, `mistral` | One-click local inference. Completely private and offline. |
| **LM Studio** | Local / Offline | Any GGUF LLM loaded in LM Studio | Local OpenAI-compatible server on `localhost:1234`. |
| **llama.cpp** | Local / Offline | Native GGUF models | High-performance standalone local execution. |
| **OpenAI-Compatible** | Cloud / Proxy | OpenRouter, SiliconFlow, Groq, Together, vLLM | Universal adapter for any OpenAI-standard endpoint. |

> [!TIP]
> **For Local Offline Gaming**: Install [Ollama](https://ollama.com/) or [LM Studio](https://lmstudio.ai/). Set the provider in ValleyTalk Reborn to `Ollama` or `LMStudio`, load a capable 7B–14B instruction-tuned model (such as `Qwen2.5` or `Llama-3.1`), and enjoy full AI immersion with **zero internet connection and zero API fees**.

---

## 📦 Requirements

- **Stardew Valley**: Version `1.6.0` or newer
- **SMAPI**: Version `4.1.0` or newer
- **Content Patcher**: Required for asset and dialogue data loading
- **Generic Mod Config Menu (GMCM)** *(Highly Recommended)*: For intuitive in-game settings adjustment

---

## 🛠️ Installation

1. Install **[SMAPI](https://smapi.io/)** (version 4.1.0+).
2. Install **[Content Patcher](https://www.nexusmods.com/stardewvalley/mods/1915)** into your `Stardew Valley/Mods` folder.
3. Download the latest **ValleyTalk Reborn** release.
4. Extract the following two folders into your `Stardew Valley/Mods` directory:
   - `ValleyTalkReborn` *(Core C# Mod assembly & UI)*
   - `[CP] ValleyTalkReborn Base` *(Base Content Pack including character bios and prompt definitions)*
5. Launch the game once through SMAPI to generate the default `config.json`.
6. Configure your preferred AI provider in-game via **Generic Mod Config Menu** or edit `config.json` directly.

---

## ⌨️ Default Controls & Hotkeys

All hotkeys can be rebound in the in-game configuration menu.

| Key | Config Property | Description |
| :---: | :--- | :--- |
| <kbd>Left Alt</kbd> | `InitiateTypedDialogueKey` | Open the free-form typed input box to speak custom words to the current NPC. |
| <kbd>G</kbd> | `DismissFollowerKey` | Toggle companion recruitment / dismiss active follower face-to-face. |
| <kbd>K</kbd> | `OpenHubMenuKey` | Open the **Integrated Management Hub** (Farmer Bio, Behavior Rules, World Settings, Advanced). |
| <kbd>J</kbd> | `OpenTimelineMenuKey` | Open the **Timeline Chronicle** (Daily logs, Memory digests, Commitments & Promises). |
| <kbd>Esc</kbd> / <kbd>Cancel</kbd> | — | Close active AI input or cancel streaming generation. |

---

## ⚙️ Configuration Guide

Configuration can be tuned in real time using **Generic Mod Config Menu (GMCM)** on the title screen or in-game pause menu.

### Essential Settings

```jsonc
{
  "EnableMod": true,                       // Master switch for the mod
  "Provider": "OpenAiCompatible",          // Active LLM provider
  "LanguageOverride": "",                  // "zh" for Chinese, or leave empty to follow game language
  "ChoiceBoxStyle": "Custom",              // "Custom" (floating cards) or "Vanilla" (classic layout)
  "TypedResponses": "With Generated",      // "With Generated", "Always", or "Never"
  "EnableSuggestedResponses": true,        // Generate quick choice replies
  "EnableRhythmicTyping": true,            // Punctuation-based typing cadence
  "EnableAmbientBarks": true,              // Proactive overhead speech bubbles
  "EnableA2A": true,                       // NPC-to-NPC autonomous chatter
  "EnablePerceptionSystem": true,          // NPC awareness of player actions
  "EnableSpouseSchedule": true,            // Dynamic outdoor schedules for spouses
  "PromptHistoryWindow": 6,                // Turns of recent chat history sent to the LLM
  "LocalMaxConcurrentRequests": 1          // Max concurrent requests for local/offline servers (1-4)
}
```

### Advanced Model Parameters

- **Temperature** (`0.0` – `2.0`): Controls creativity and variability. Default: `0.9`.
- **TopP** (`0.0` – `1.0`): Nucleus sampling probability threshold. Default: `0.9`.
- **MaxTokens** (`100` – `8192`): Maximum response token length. Default: `1024`.
- **QueryTimeout** (`5` – `120` seconds): Network timeout for LLM responses. Default: `60`.
- **CustomBodyJson**: Inject custom JSON payload parameters for OpenAI-compatible endpoints (e.g., custom system prompts or frequency penalties).

---

## 🖥️ In-Game Management Hub & Chronicle

### The Integrated Hub (<kbd>K</kbd>)
Press <kbd>K</kbd> in-game to bring up the 4-in-1 management console:
1. **Farmer Profile**: Set your farmer's persona, pronouns, sexual orientation, and customized background notes so NPCs address you properly.
2. **Behavior Rules & Multi-Rules**: Establish specific personality constraints, custom nicknames, or inside jokes for specific NPCs.
3. **World Settings & POI Tuning**: Configure date ambience settings, festival dialogue keys, and point-of-interest sensitivity.
4. **Advanced Settings**: Fine-tune perception triggers, memory retention count, and dialogue throttles.

### The Timeline Chronicle (<kbd>J</kbd>)
Press <kbd>J</kbd> to inspect the memory tapestry of your village:
- **Daily Dialogue Journals**: Review past conversations organized chronologically by game day.
- **Weekly & Seasonal Distillations**: Read AI-condensed summaries of how your friendship evolved over seasons.
- **Promise Ledger**: Track commitments and promises made during conversations.

---

## 💻 Console Commands

Open the SMAPI console while playing to access helpful management and debugging tools:

| Command | Usage | Description |
| :--- | :--- | :--- |
| `vt_promises` | `vt_promises <npcName>` | Lists active unfulfilled promises made to/by the specified NPC. |
| `vt_fulfill` | `vt_fulfill <npcName> <memoryId>` | Manually marks a specific promise as fulfilled. |
| `vt_archive` | `vt_archive <npcName>` | Views the archived long-term memories stored for an NPC. |
| `vt_emotion` | `vt_emotion dump <npcName>` | Displays current emotional vector (V, A, O), shocks, and today's scene. |
| `vt_emotion` | `vt_emotion force_scene <npc> <id>` | Forces a specific daily scene on the NPC for testing. |
| `vt_emotion` | `vt_emotion add_shock <npc> <dv> <da> <do> <min>` | Injects an emotional shock with specific Valence, Arousal, and Openness deltas. |
| `vt_emotion` | `vt_emotion clear <npcName>` | Clears all active emotional shocks on an NPC. |
| `vt_extract_test` | `vt_extract_test [npcName]` | Runs an immediate diagnostic test on the memory extraction pipeline. |

---

## 🎨 Authoring Custom NPC Bios

ValleyTalk Reborn comes pre-configured with rich personality cards for **all vanilla Stardew Valley villagers**.

Want to add AI dialogue to modded NPCs (such as *Stardew Valley Expanded* or *Ridgeside Village*)? You can author content pack files!

### Bio Structure Overview
Character cards reside in `[CP] ValleyTalkReborn Base/assets/bio/<CharacterName>.json`:
- **Identity & Background**: Core persona, occupation, social standing, and psychological tensions.
- **Voice & Habits**: Speech mannerisms, vocabulary habits, and tone anchors.
- **Observation Lenses**: How the character perceives their surroundings and the player.
- **Stage Progression Ladder**: Distinct behavioral stages (e.g., Acquaintance, Friend, Close Friend, Dating, Married) with stage-specific preoccupation pools.

> [!NOTE]
> Detailed guidelines for creating custom NPC profiles can be found in [`Character Bio Authoring & Configuration Guide.json`](./ContentPack/assets/bio/%23%20ValleyTalk%20Reborn%20%E2%80%94%20Character%20Bio%20Authoring%20&%20Configuration%20Guide.json).

---

## 🧩 Compatibility & Extensibility

- **Content Patcher (CP)**: Fully compatible. Character cards, date locations, and system prompts can be patched or overridden by third-party Content Packs.
- **Polyamory Sweet / Polyamory Mods**: Built-in compatibility support for multi-spouse and polyamorous playthroughs.
- **Mod Author AI Consent**: Includes a strict `RespectAuthorAiConsent` safety toggle. By default, third-party custom NPCs that explicitly disallow AI usage remain untouched.
- **Cross-Platform**: Tested on Windows, macOS, Linux, and includes platform compatibility handling for Android environments.
- **Custom Font Rendering**: Features high-fidelity embedded fonts (**Roboto Slab** & **HarmonyOS Sans SC**) rendered via FontStashSharp for clean typography across resolutions.

---

## ❓ FAQ & Troubleshooting

<details>
<summary><strong>Q: Does this mod use a lot of tokens / cost money?</strong></summary>

A: When using paid cloud providers (OpenAI, Claude, etc.), API usage incurs standard platform fees based on your provider plan. However, ValleyTalk Reborn is heavily optimized with prompt slimming and token compression. If you want **completely free and private gameplay**, configure **Ollama** or **LM Studio** to run local models on your PC.
</details>

<details>
<summary><strong>Q: Why is an NPC repeating previous dialogue or talking slowly?</strong></summary>

A: 
1. Check your network connection and API latency if using a cloud provider.
2. If using a local model, verify your GPU has sufficient VRAM to run the model without CPU offload bottlenecking.
3. Check `PromptHistoryWindow` in configuration—lowering it reduces prompt size and speeds up response times.
</details>

<details>
<summary><strong>Q: Can I use this mod offline?</strong></summary>

A: Yes! Simply select `Ollama` or `LMStudio` in the settings, load any supported open-source model locally, and disconnect from the internet. ValleyTalk Reborn will function seamlessly offline.
</details>

<details>
<summary><strong>Q: How do I disable AI dialogue for specific villagers?</strong></summary>

A: In GMCM or `config.json`, add character names separated by commas to the `DisableCharacters` setting (e.g. `"DisableCharacters": "Pierre, Lewis"`).
</details>

---

## 📄 License & Acknowledgments

- **Code License**: Licensed under the [GNU General Public License v3.0 (GPL-3.0)](LICENSE.txt).
- **Game Assets & IP**: All original Stardew Valley game assets, characters, lore, and audio belong to **ConcernedApe**.
- **Community**: Special thanks to the Stardew Valley modding community, the SMAPI development team, and all early testers who helped shape Pelican Town's living dialogue.

---

<p align="center">
  <em>Make Pelican Town feel like home again. Happy farming! 🌾</em>
</p>

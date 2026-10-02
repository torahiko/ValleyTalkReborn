using System;
using System.Text;
using ValleytalkReborn.Cutscene.Perception;

namespace ValleytalkReborn.Cutscene.Prompt
{
    /// <summary>
    /// 虚拟导演提示词构建器：负责将现实环境上下文与剧情意图转化为符合 Schema 契约的 Prompt
    /// </summary>
    public static class CutscenePromptBuilder
    {
        public static string BuildSystemPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("你是一名精通《星露谷物语》（Stardew Valley）游戏生态的电影虚拟导演（Virtual Cutscene Director）。");
            sb.AppendLine("你的职责是调度场景中的 NPC 进行走位、运镜、表情反馈与交互对白，构思一段起承转合自然、富有生活戏剧感的微型过场演出（15~25 秒）。");
            sb.AppendLine();
            sb.AppendLine("【必须严格遵循的输出规范】：");
            sb.AppendLine("1. 只能输出纯 JSON 对象（用 ```json 包裹），严禁输出任何多余的开场白、问候语或解释文字。");
            sb.AppendLine("2. 格式必须完全符合以下 JSON Schema：");
            sb.AppendLine(@"{
  ""title"": ""剧本标题"",
  ""actors"": [""参演NPC英文名""],
  ""actions"": [
    { ""type"": ""camera"", ""target"": ""NPC英文名或farmer"", ""waitForCompletion"": false },
    { ""type"": ""move"", ""actor"": ""NPC英文名"", ""targetTile"": [x, y], ""timeout"": 6.0, ""waitForCompletion"": true },
    { ""type"": ""lookAt"", ""actor"": ""NPC英文名"", ""target"": ""目标NPC名或farmer"", ""waitForCompletion"": true },
    { ""type"": ""face"", ""actor"": ""NPC英文名"", ""direction"": 2, ""waitForCompletion"": true },
    { ""type"": ""emote"", ""actor"": ""NPC英文名"", ""emote"": ""SURPRISE|HEART|QUESTION|HAPPY|SAD|ANGRY"", ""waitForCompletion"": false },
    { ""type"": ""speak"", ""actor"": ""NPC英文名"", ""text"": ""角色台词"", ""waitForCompletion"": true },
    { ""type"": ""wait"", ""duration"": 0.8, ""waitForCompletion"": true },
    { ""type"": ""sound"", ""soundName"": ""dwop|coin|purchase"", ""waitForCompletion"": false }
  ]
}");
            sb.AppendLine();
            sb.AppendLine("【导演纪律与视听技巧】：");
            sb.AppendLine("- 【镜头开场铁律】：全剧第 1 个动作必须通常安排 camera 动作，将 target 设定为开场说话或行动的 NPC（如 target: \"NPC英文名\"），将镜头平滑推向舞台主角！");
            sb.AppendLine("- 【玩家互动规则】：当只有 1 位 NPC 参演时，剧情是该 NPC 与农夫（farmer）的面对面对手戏。NPC 必须移动走向农夫身旁（targetTile 选用【农夫互动交谈位】坐标），停在农夫身边并面向农夫（lookAt target: \"farmer\"），再对农夫说话。");
            sb.AppendLine("- 【空间锚定铁律】：move 动作的 targetTile 必须且只能选用用户提示中给出的【现场可用地标坐标】或其临近格子，绝对禁止捏造不存在或越界的坐标！");
            sb.AppendLine("- 【视听并发法则】：积极运用 \"waitForCompletion\": false 编排并行演出（例如镜头推向角色的同时角色冒出感叹号并起步走位；或者说话的同时播放音效并冒爱心）。");
            sb.AppendLine("- 【角色互动运镜】：在多角色对话中，积极运用 camera 动作在不同角色间切换对焦，使观众视线始终聚焦在当前核心演出的角色身上。");
            sb.AppendLine("- 【对白风格】：台词必须贴合角色性格背景与人设口吻，短小精炼，生动有趣。全剧通常包含 6~12 个动作步骤。");

            return sb.ToString();
        }

        public static string BuildUserPrompt(CutsceneContext ctx, string userIntent)
        {
            var sb = new StringBuilder();

            sb.AppendLine("### 当前游戏环境与场景信息：");
            sb.AppendLine($"- 场景地点：{ctx.LocationFriendlyName} ({ctx.LocationName})");
            sb.AppendLine($"- 时间节气：{ctx.Season} 第 {ctx.DayOfMonth} 天, 时间 {ctx.TimeOfDay:D4}, 天气: {ctx.Weather}");
            if (!string.IsNullOrWhiteSpace(ctx.FestivalName))
            {
                sb.AppendLine($"- 今日节日：{ctx.FestivalName}");
            }
            sb.AppendLine($"- 农夫（玩家）当前站立坐标：({(int)ctx.FarmerTile.X}, {(int)ctx.FarmerTile.Y})");
            sb.AppendLine();

            sb.AppendLine("### 参演演员列表与人设：");
            foreach (var actor in ctx.Actors)
            {
                sb.AppendLine($"- {actor.Name} ({actor.DisplayName})：");
                sb.AppendLine($"  * 当前位置坐标：({(int)actor.CurrentTile.X}, {(int)actor.CurrentTile.Y}), 朝向: {actor.FacingDirection}");
                sb.AppendLine($"  * 与农夫好感度：{actor.HeartLevel} 心 ({actor.FriendshipPoints} 点)");
                if (!string.IsNullOrWhiteSpace(actor.PersonalitySummary))
                {
                    sb.AppendLine($"  * 人设与性格特征：{actor.PersonalitySummary}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("### 现场可用地标与合法走位坐标（move 动作必须选用此处的坐标）：");
            if (ctx.NearbyPois.Count > 0)
            {
                foreach (var poi in ctx.NearbyPois)
                {
                    sb.AppendLine($"- [{(int)poi.Tile.X}, {(int)poi.Tile.Y}] : {poi.Description}");
                }
            }
            else
            {
                // 无特定 POI 时，提供演员前方与农夫身旁的相对合法格
                var actor = ctx.Actors[0];
                sb.AppendLine($"- [{(int)actor.CurrentTile.X + 2}, {(int)actor.CurrentTile.Y}] : 角色前方开阔区域");
                sb.AppendLine($"- [{(int)ctx.FarmerTile.X + 1}, {(int)ctx.FarmerTile.Y}] : 农夫身旁交谈位");
            }
            sb.AppendLine();

            sb.AppendLine("### 导演剧情意图要求：");
            if (!string.IsNullOrWhiteSpace(userIntent))
            {
                sb.AppendLine($"【指定主题】：{userIntent}");
            }
            else
            {
                sb.AppendLine("【指定主题】：角色之间的即兴日常微型互动（根据当下的场景气氛与性格特征自由发挥）。");
            }

            if (ctx.Actors.Count > 0)
            {
                sb.AppendLine($"【运镜指引】：首个动作请用 camera 动作将镜头切向开场主角（如 target: \"{ctx.Actors[0].Name}\"），使观众视线正对舞台中心。");
            }

            if (ctx.Actors.Count == 1)
            {
                var soloActor = ctx.Actors[0];
                sb.AppendLine();
                sb.AppendLine("【单人互动核心导演纪律】：");
                sb.AppendLine($"- 当前场景仅有 1 位参演村民（{soloActor.DisplayName}），这场戏是 {soloActor.DisplayName} 与农夫（玩家/farmer）之间的专属对手戏！");
                sb.AppendLine($"- 走位铁律：{soloActor.DisplayName} 必须主动走向农夫身旁（move 动作的 targetTile 必须选用【农夫互动交谈位】坐标），停在农夫身边（1~2 格距离），然后执行 lookAt target: \"farmer\" 面对农夫，再表达问候、关心或倾诉！");
            }

            sb.AppendLine();
            sb.AppendLine("请立即构思剧目并输出符合 Schema 的纯 JSON 剧本：");

            return sb.ToString();
        }
    }
}

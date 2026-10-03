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
    { ""type"": ""sound"", ""soundName"": ""dwop|coin|purchase"", ""waitForCompletion"": false },
    {
      ""type"": ""choice"",
      ""actor"": ""NPC英文名"",
      ""prompt"": ""提示农夫进行选择的情境提问"",
      ""options"": [
        {
          ""text"": ""积极或赞同的选项文字"",
          ""friendship"": 15,
          ""feedback"": ""NPC中文名更喜欢你了。"",
          ""actions"": [
            { ""type"": ""emote"", ""actor"": ""NPC英文名"", ""emote"": ""HEART"", ""waitForCompletion"": false },
            { ""type"": ""speak"", ""actor"": ""NPC英文名"", ""text"": ""高兴的后续反馈台词"", ""waitForCompletion"": true }
          ]
        },
        {
          ""text"": ""委婉或不同意的选项文字"",
          ""friendship"": -10,
          ""feedback"": ""NPC中文名有点难过。"",
          ""actions"": [
            { ""type"": ""emote"", ""actor"": ""NPC英文名"", ""emote"": ""SAD"", ""waitForCompletion"": false },
            { ""type"": ""speak"", ""actor"": ""NPC英文名"", ""text"": ""失落或尴尬的后续反馈台词"", ""waitForCompletion"": true }
          ]
        }
      ]
    }
  ]
}");
            sb.AppendLine();
            sb.AppendLine("【导演纪律与视听技巧】：");
            sb.AppendLine("- 【镜头开场铁律】：全剧第 1 个动作必须通常安排 camera 动作，将 target 设定为开场说话或行动的 NPC（如 target: \"NPC英文名\"），将镜头平滑推向舞台主角！");
            sb.AppendLine("- 【开场定格铁律】：参演角色在全剧第 0 帧已自动就位于农夫身前/身旁（半径 2 格内）并面向农夫，无需任何入场移动；剧本应直接以 camera 对焦 + emote/face/speak 开场；move 仅是叙事演进中的情绪调度（踱步、凑近耳语、转身离开），单次位移不超过 3~4 格。");
            sb.AppendLine("- 【分支选择规则（choice）】：choice 动作代表剧情在此刻需要农夫亲口表态才能继续——受话人始终是农夫本人。在编排 choice 前，先确认剧情确实发展到了农夫被卷入的节点，例如村民向农夫发出邀请、请求帮助或询问农夫本人的看法；choice 的 prompt 必须是向农夫（\"你\"）提出的问题或请求。当演出是村民之间的自然交流、或村民的问候与单向倾诉时，镜头与台词始终聚焦在村民身上，以村民的表情、走位与对白自然收尾。options 提供 2 个性格鲜明的分支选项（一正一负或不同态度），每个选项包含 text、friendship（好感增减，-20 ~ +20）、feedback（左下角浮动提示，格式必须类似'NPC名更喜欢你了。'或'NPC名有点难过。'）以及被选后触发的 actions 子动作列表（通常 1~2 个 emote/speak 动作）。");
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
                sb.AppendLine($"- 开场定格：{soloActor.DisplayName} 开场即已立于农夫身旁并面向农夫，直接以表情（emote）与台词（speak）开场，无需任何入场移动；如需走位仅作 3 格内的情绪性移动（踱步、凑近耳语、转身离开）。");
                sb.AppendLine($"- 互动分支：当 {soloActor.DisplayName} 向农夫提出只有农夫才能回答的问题（如邀请、请求或询问农夫的看法）时，在提问后编排 1 个 choice 动作（2 个选项，附带合理的 friendship [-20~+20] 与 feedback 提示，例如'{soloActor.DisplayName}更喜欢你了。'），并为每个选项编写 NPC 的后续反应动作；当这场戏是 {soloActor.DisplayName} 的问候、关怀或单向倾诉时，以 {soloActor.DisplayName} 的表情与台词自然收尾即可。");
            }
            else if (ctx.Actors.Count > 1)
            {
                sb.AppendLine("【分支互动提示】：choice 动作只属于村民向农夫提出只有农夫能回答的问题、剧情走向需要农夫表态的节点；村民之间的交流通过 lookAt、emote 与对白自然衔接，镜头始终聚焦交流中的村民。");
            }

            sb.AppendLine();
            sb.AppendLine("请立即构思剧目并输出符合 Schema 的纯 JSON 剧本：");

            return sb.ToString();
        }
    }
}

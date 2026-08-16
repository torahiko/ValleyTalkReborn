using System;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    /// <summary>
    /// 负责约会邀请的完整生命周期：///   1. 向 Data/Mail 注入所有约会信件模板（AssetRequested）
    ///   2. 每天早晨随机向玩家邮箱投递一封邀请（DayStarted）
    ///   3. 玩家打开信件时激活对应的 DateManager 预约（MenuChanged）
    /// DateManager 不再触碰任何邮件逻辑。
    /// </summary>
    public sealed class InvitationManager
    {
        // ─── Singleton ───────────────────────────────────────────────
        public static readonly InvitationManager Instance = new();
        private InvitationManager() { }

        // 配合 DateManager 使用的 NPC 候选名单（与原版保持一致）
        private static readonly string[] DateCandidates =
        {
            "Abigail", "Alex", "Elliott", "Emily", "Haley", "Harvey",
            "Leah", "Maru", "Penny", "Sam", "Sebastian", "Shane"
        };

        // MailId 前缀，供解析时识别
        private const string MailPrefix = "ValleyTalk@Date@";

        private static bool IsChineseLanguage =>
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        // ─── 初始化 / 清理 ───────────────────────────────────────────

        /// <summary>由 ModEntry.Entry() 显式调用，注册所有事件。</summary>
        public void Initialize(IModHelper helper)
        {
            helper.Events.Content.AssetRequested += OnAssetRequested;
            helper.Events.GameLoop.DayStarted     += OnDayStarted;
            helper.Events.Display.MenuChanged     += OnMenuChanged;
        }

        public void Cleanup(IModHelper helper)
        {
            helper.Events.Content.AssetRequested -= OnAssetRequested;
            helper.Events.GameLoop.DayStarted     -= OnDayStarted;
            helper.Events.Display.MenuChanged     -= OnMenuChanged;
        }

        // ─── 1. 注入邮件模板 ─────────────────────────────────────────

        private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
        {
            if (!e.NameWithoutLocale.IsEquivalentTo("Data/Mail"))
                return;

            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, string>().Data;
                bool isZh = IsChineseLanguage;

                foreach (var loc in DateLocationRegistry.Locations.Values)
                {
                    foreach (var npcName in DateCandidates)
                    {
                        string mailId  = BuildMailId(npcName, loc.LocationId);
                        string locName = isZh ? loc.DisplayNameZh : loc.DisplayNameEn;

                        data[mailId] = isZh
                            ? $"亲爱的 @：^^最近农场忙碌，不知你今晚是否有空？^" +
                              $"如果可以的话，晚些时候来 {locName} 找我吧。^" +
                              $"我想和你单独待一会儿。^^—— 期待见你的 {npcName} %item null %%[#]今晚的约会邀请"
                            : $"Dear @,^^I know you've been working hard on the farm, " +
                              $"and I was wondering if you're free tonight?^" +
                              $"If you can make it, come find me at {locName} tonight.^" +
                              $"I'd love to spend some time alone with you.^^" +
                              $"Yours, {npcName} %item null %%[#]An Invitation for Tonight";
                    }
                }
            });
        }

        // ─── 2. 晨间投递 ─────────────────────────────────────────────

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            TryDeliverMorningInvitation();
        }

        private void TryDeliverMorningInvitation()
        {
            if (Utility.isFestivalDay(Game1.dayOfMonth, Game1.season))
                return;

            var farmer     = Game1.player;
            bool isMarried = farmer.getSpouse() != null;
            bool allowPoly = IsPolyamoryModInstalled();

            // 已婚且未开启多配偶 mod：15% 概率触发配偶口头邀请
            if (isMarried)
            {
                string spouseName = farmer.getSpouse()?.Name;
                if (!string.IsNullOrEmpty(spouseName) && DateLocationRegistry.Locations.Count > 0)
                {
                    if (Game1.random.NextDouble() < 0.15)
                    {
                        // 设置标志，让配偶 NPC 的对话系统在早上触发口头邀请
                        DateManager.Instance.SpouseMorningInvitePending = true;
                        ModEntry.SMonitor?.Log(
                            $"[InvitationManager] Spouse {spouseName} will invite verbally today.",
                            LogLevel.Info);
                        return;
                    }
                }
            }

            // 已婚但不允许多配偶：不向其他 NPC 发信
            if (isMarried && !allowPoly)
                return;

            // 恋爱中的 NPC：15% 概率发信
            var datingCandidates = farmer.friendshipData.Pairs
                .Where(p => p.Value.Points >= 2000 && p.Value.IsDating())
                .Select(p => p.Key)
                .ToList();

            if (datingCandidates.Count == 0 || !(Game1.random.NextDouble() < 0.15))return;

            string winner    = datingCandidates[Game1.random.Next(datingCandidates.Count)];
            var    locList   = DateLocationRegistry.Locations.Keys.ToList();
            string targetLoc = locList[Game1.random.Next(locList.Count)];
            string mailId    = BuildMailId(winner, targetLoc);

            // 每天用带日期后缀的 key 做去重，避免同组合永久封堵
            string todayKey = $"{mailId}_{Game1.year}_{Game1.currentSeason}_{Game1.dayOfMonth}";

            if (!farmer.mailReceived.Contains(todayKey) && !farmer.mailbox.Contains(mailId))
            {
                farmer.mailbox.Add(mailId);
                farmer.mailReceived.Add(todayKey);
                ModEntry.SMonitor?.Log(
                    $"[InvitationManager] Date mail placed in mailbox: {mailId}.",
                    LogLevel.Info);
            }
        }

        // ─── 3. 信件阅读激活 ─────────────────────────────────────────

        private void OnMenuChanged(object sender, MenuChangedEventArgs e)
        {
            if (e.NewMenu is not LetterViewerMenu letterMenu)
                return;

            string mailTitle = letterMenu.mailTitle;
            if (string.IsNullOrEmpty(mailTitle) || !mailTitle.StartsWith(MailPrefix))
                return;

            // 格式：ValleyTalk@Date@{NpcName}@{LocationId}
            string[] parts = mailTitle.Split('@', 4);
            if (parts.Length != 4)
                return;

            string npcName = parts[2];
            string locId   = parts[3];

            NPC npc = Game1.getCharacterFromName(npcName);
            if (npc == null)
                return;

            // 只有当前无进行中约会时才激活
            if (DateManager.Instance.Phase != DatePhase.None)
            {
                ModEntry.SMonitor?.Log(
                    $"[InvitationManager] Ignored mail activation: DateManager already in phase {DateManager.Instance.Phase}.",
                    LogLevel.Debug);
                return;
            }

            // 信件来自 NPC 主动约，不赴约不算放鸽子
            if (DateManager.Instance.TryScheduleDate(npc, locId, DateManager.DateOrigin.NpcInitiated))
            {
                ModEntry.SMonitor?.Log(
                    $"[InvitationManager] Player read mail. NPC-initiated date scheduled: {npcName} @ {locId}.",
                    LogLevel.Info);
            }
        }

        // ─── 工具方法 ─────────────────────────────────────────────────

        /// <summary>构造标准 MailId，格式 ValleyTalk@Date@{npcName}@{locationId}。</summary>
        public static string BuildMailId(string npcName, string locationId)=> $"{MailPrefix}{npcName}@{locationId}";

        private static bool IsPolyamoryModInstalled()
        {
            var registry = ModEntry.SHelper?.ModRegistry;
            if (registry == null) return false;
            return registry.IsLoaded("Platonymous.CustomSpouseRooms")
                || registry.IsLoaded("spacechase0.MultipleSpouses")
                || registry.IsLoaded("MissCoriel.FreeLove")
                || registry.IsLoaded("ApryllForever.PolyamorySweet")
                || registry.IsLoaded("PeacefulEnd.PolyamorySweetReborn");
        }
    }
}
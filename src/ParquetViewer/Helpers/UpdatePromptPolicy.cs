using System;

namespace ParquetViewer.Helpers
{
    /// <summary>
    /// 决定启动时是否需要向用户提示「发现新版本」。
    /// 这里只保留与界面无关的纯逻辑，便于单元测试覆盖各种版本组合。
    /// </summary>
    public static class UpdatePromptPolicy
    {
        /**
         * @brief 判断是否需要弹出更新提示窗口
         * @param currentVersion 当前程序版本号
         * @param latestReleaseVersion 远端最新正式版本号，查询失败时为 null
         * @param skippedVersion 用户此前勾选「记住我的选择」并取消时记下的版本号，可为 null
         * @return 需要提示返回 true，否则返回 false
         * @note 用户只跳过被记住的那一个版本；远端出现更新的版本时会重新提示
         */
        public static bool ShouldPrompt(SemanticVersion currentVersion, SemanticVersion? latestReleaseVersion, string? skippedVersion)
        {
            if (latestReleaseVersion is not SemanticVersion latestVersion)
            {
                return false;
            }

            //远端没有比当前更新的版本，无需提示
            if (latestVersion <= currentVersion)
            {
                return false;
            }

            //用户记住的正是本次要提示的版本，跳过；记住的是更旧的版本则照常提示
            if (skippedVersion is not null
                && SemanticVersion.TryParse(skippedVersion, out var skipped)
                && skipped == latestVersion)
            {
                return false;
            }

            return true;
        }
    }
}
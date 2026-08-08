using Skil.Content;
using Skil.Utils.quickBuild;
using tContentPatch;
using Terraria.UI;

namespace Skil
{
    internal class SettingUI_SkilList1 : ModSetting
    {
        public override string Name => "播放音乐";
        public override string Title => "midiplayer: 播放音乐";

        public override UIElement GetUI()
        {
            return UIBuild.get3(SkilListControl1.GetUI());
        }
    }
}
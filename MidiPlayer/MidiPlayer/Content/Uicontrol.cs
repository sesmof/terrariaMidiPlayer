using CommandHelp;
using Skil.Utils;
using Skil.Utils.quickBuild;
using System.Collections.Generic;
using Terraria.UI;

namespace Skil.Content
{
    public class SkilListControl1
    {
        public static GetSetReset<int> damage = new GetSetReset<int>();
        public static GetSetReset<bool> aimAdvance = new GetSetReset<bool>();
        public static GetSetReset<float> aimAdvance_val = new GetSetReset<float>(38, 38);

        public static List<CommandObject> GetCO()
        {
            List<CommandObject> cos = new List<CommandObject>();
            cos.Add(new CommandHRA<int>("damage", damage, new CommandInt()));
            cos.Add(CommandBuild.get1("aimAdvance", aimAdvance, aimAdvance_val, new CommandFloat()));
            cos.AddRange(musicplay.GetCO());
            

            return cos;
        }

        public static List<UIElement> GetUI()
        {
            List<UIElement> uis = new List<UIElement>();

            uis.AddRange(musicplay.GetUI());


            return uis;
        }
    }
}
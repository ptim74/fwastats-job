using System;
using System.Collections.Generic;
using System.Text;

namespace FWAStatsJobCore
{
    public class UpdateIndexView
    {
        public List<string> errors { get; set; }
        public List<UpdateTask> tasks { get; set; }
    }
}

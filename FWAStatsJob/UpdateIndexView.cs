using System.Collections.Generic;

namespace FWAStatsJobCore;

public class UpdateIndexView
{
    public List<string> errors { get; set; }
    public List<UpdateTask> tasks { get; set; }
}

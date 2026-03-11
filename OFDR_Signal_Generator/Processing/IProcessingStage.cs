using OFDR_Signal_Generator.Models;

namespace OFDR_Signal_Generator.Processing
{
    public interface IProcessingStage
    {
        string Name { get; }

        StageResult Execute(StageContext context);
    }
}

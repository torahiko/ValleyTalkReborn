using System.Threading.Tasks;

namespace ValleytalkReborn;

internal interface IGetModelNames
{
    Task<string[]> GetModelNamesAsync();
}
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleytalkReborn
{
    public interface IPolyamorySweetApi
    {
        Dictionary<string, NPC> GetSpouses(Farmer farmer, bool all = false);
    }

    public interface ISweetRoomsAPI
    {
        Point GetSpouseTileOffset(NPC spouse);
        Point GetSpouseTile(NPC spouse);
        Point GetSpouseRoomCornerTile(NPC spouse);
        void ResetRooms(GameLocation location);
    }
}

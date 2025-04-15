using SpacetimeDB;

public static partial class Module
{
    [Table(Name = "player", Public = true)]
    [Table(Name = "logged_out_player")]
    public partial struct Player
    {
        [PrimaryKey]
        public Identity identity;
        [Unique, AutoInc]
        public uint player_id;
        public string name;
    }

    [Table(Name = "game", Public = true)]
    public partial struct Game
    {
        [PrimaryKey, AutoInc]
        public uint game_id;
        public uint player1_id;
        public uint player2_id;
        public uint current_turn_id;
        public bool game_started;
        public bool game_ended;
        public uint winner_id;
    }

    [Table(Name = "board_tile", Public = true)]
    public partial struct BoardTile
    {
        [PrimaryKey, AutoInc]
        public uint tile_id;
        [SpacetimeDB.Index.BTree]
        public uint game_id;
        public uint owner_id;
        public uint x;
        public uint y;
        public bool has_ship;
        public bool hit;
    }

    [Table(Name = "ship", Public = true)]
    public partial struct Ship
    {
        [PrimaryKey, AutoInc]
        public uint ship_id;
        [SpacetimeDB.Index.BTree]
        public uint game_id;
        public uint owner_id;
        public uint length;
        public uint hit_count;
        public bool sunk;
    }

    [Reducer(ReducerKind.ClientConnected)]
    public static void Connect(ReducerContext ctx)
    {
        var player = ctx.Db.logged_out_player.identity.Find(ctx.Sender);
        if (player != null)
        {
            ctx.Db.player.Insert(player.Value);
            ctx.Db.logged_out_player.identity.Delete(player.Value.identity);
        }
        else
        {
            ctx.Db.player.Insert(new Player
            {
                identity = ctx.Sender,
                name = "",
            });
        }
    }

    [Reducer(ReducerKind.ClientDisconnected)]
    public static void Disconnect(ReducerContext ctx)
    {
        var player = ctx.Db.player.identity.Find(ctx.Sender) ?? throw new Exception("Player not found");
        // Forfeit the game if player is still in game
        ctx.Db.logged_out_player.Insert(player);
        ctx.Db.player.identity.Delete(player.identity);
    }

    [Reducer]
    public static void CreateGame(ReducerContext ctx, uint opponent_id)
    {
        var player = ctx.Db.player.identity.Find(ctx.Sender) ?? throw new Exception("Player not found");

        ctx.Db.game.Insert(new Game {
            player1_id = player.player_id,
            player2_id = opponent_id,
            current_turn_id = player.player_id,
            game_started = false,
            game_ended = false
        });
    }

    [Reducer]
    public static void PlaceShip(ReducerContext ctx, uint game_id, uint x, uint y, uint length, bool horizontal)
    {
        var player = ctx.Db.player.identity.Find(ctx.Sender) ?? throw new Exception("Player not found");

        var game = ctx.Db.game.game_id.Find(game_id);
        if (!game.HasValue || game.Value.game_started) return;

        ctx.Db.ship.Insert(new Ship {
            game_id = game_id,
            owner_id = player.player_id,
            length = length,
            hit_count = 0,
            sunk = false
        });

        for (uint i = 0; i < length; i++)
        {
            uint posX = horizontal ? x + i : x;
            uint posY = horizontal ? y : y + i;
            ctx.Db.board_tile.Insert(new BoardTile {
                game_id = game_id,
                owner_id = player.player_id,
                x = posX,
                y = posY,
                has_ship = true,
                hit = false
            });
        }
    }

    [Reducer]
    public static void FireAt(ReducerContext ctx, uint game_id, uint x, uint y)
    {
        var player = ctx.Db.player.identity.Find(ctx.Sender) ?? throw new Exception("Player not found");
        var game = ctx.Db.game.game_id.Find(game_id) ?? throw new Exception("Game not found");
        if (game.game_ended || game.current_turn_id != player.player_id) return;

        var opponent = (player.player_id == game.player1_id) ? game.player2_id : game.player1_id;

        foreach (var t in ctx.Db.board_tile.game_id.Filter(game_id))
        {
            var tile = t;
            if (tile.owner_id == opponent && tile.x == x && tile.y == y)
            {
                if (!tile.hit)
                {
                    tile.hit = true;
                    ctx.Db.board_tile.tile_id.Update(tile);

                    if (tile.has_ship)
                    {
                        foreach (var s in ctx.Db.ship.game_id.Filter(game_id))
                        {
                            var ship = s;
                            if (ship.owner_id == opponent)
                            {
                                // Roughly count as hit if the tile belongs to a ship
                                ship.hit_count++;
                                if (ship.hit_count >= ship.length)
                                {
                                    ship.sunk = true;
                                }
                                ctx.Db.ship.ship_id.Update(ship);
                            }
                        }
                    }
                }
                break;
            }
        }

        // Check if all ships of the opponent are sunk
        bool allSunk = true;
        foreach (var ship in ctx.Db.ship.Iter())
        {
            if (ship.game_id == game_id && ship.owner_id == opponent && !ship.sunk)
            {
                allSunk = false;
                break;
            }
        }

        if (allSunk)
        {
            game.game_ended = true;
            game.winner_id = player.player_id;
        }
        else
        {
            game.current_turn_id = opponent;
        }

        ctx.Db.game.game_id.Update(game);
    }
}
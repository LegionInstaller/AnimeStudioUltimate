using System;
using System.Linq;
using System.Collections.Generic;
using static AnimeStudio.CryptoHelper;
using System.IO;
using System.Text.Json;

namespace AnimeStudio
{
    public static class GameManager
    {
        private static Dictionary<int, Game> Games = new Dictionary<int, Game>();

        static GameManager()
        {
            LoadGames();
        }

        public static void ReloadGames() => LoadGames();

        private static void LoadGames()
        {
            Games.Clear();
            int index = 0;

            // main games
            Games.Add(index++, new(GameType.Normal, "Unity", GameCategory.Unity));
            Games.Add(index++, new(GameType.UnityCN, "Unity CN", GameCategory.Hidden));
            Games.Add(index++, new Mhy(GameType.GI, "Live", GIMhyShiftRow, GIMhyKey, GIMhyMul, GIExpansionKey, GISBox, GIInitVector, GIInitSeed));
            Games.Add(index++, new Mr0k(GameType.GI_Pack, "Pack", PackExpansionKey, blockKey: PackBlockKey));
            Games.Add(index++, new Mr0k(GameType.GI_CB1, "CBT 1"));
            Games.Add(index++, new Blk(GameType.GI_CB2, "CBT 2", GI_CBXExpansionKey, initVector: GI_CBXInitVector, initSeed: GI_CBXInitSeed));
            Games.Add(index++, new Blk(GameType.GI_CB3, "CBT 3", GI_CBXExpansionKey, initVector: GI_CBXInitVector, initSeed: GI_CBXInitSeed));
            Games.Add(index++, new Mhy(GameType.GI_CB3Pre, "CBT 3 Pre", GI_CBXMhyShiftRow, GI_CBXMhyKey, GI_CBXMhyMul, GI_CBXExpansionKey, GI_CBXSBox, GI_CBXInitVector, GI_CBXInitSeed));
            Games.Add(index++, new Mr0k(GameType.BH3, "Live", BH3ExpansionKey, BH3SBox, BH3InitVector, BH3BlockKey));
            Games.Add(index++, new Mr0k(GameType.BH3Pre, "Pre", PackExpansionKey, blockKey: PackBlockKey));
            Games.Add(index++, new Mr0k(GameType.BH3PrePre, "Pre Pre", PackExpansionKey, blockKey: PackBlockKey));
            Games.Add(index++, new Mr0k(GameType.SR, "Live", Mr0kExpansionKey, initVector: Mr0kInitVector, blockKey: Mr0kBlockKey));
            Games.Add(index++, new Mr0k(GameType.SR_CB2, "CBT 2", Mr0kExpansionKey, initVector: Mr0kInitVector, blockKey: Mr0kBlockKey));
            Games.Add(index++, new Mhy(GameType.ZZZ, "Live", GIMhyShiftRow, GIMhyKey, GIMhyMul, null, GISBox, null, 0uL));
            Games.Add(index++, new Mr0k(GameType.ZZZ_CB1, "CBT 1", Mr0kExpansionKey, initVector: Mr0kInitVector, blockKey: Mr0kBlockKey));
            Games.Add(index++, new Mhy(GameType.ZZZ_CB2, "CBT 2", GIMhyShiftRow, GIMhyKey, GIMhyMul, null, GISBox, null, 0uL));
            Games.Add(index++, new Game(GameType.HNA_CB1, "CBT 1", GameCategory.Hoyo));
            Games.Add(index++, new Game(GameType.HYG_CB1, "CBT 1", GameCategory.Hoyo));
            Games.Add(index++, new Mr0k(GameType.TOT, "Live", Mr0kExpansionKey, initVector: Mr0kInitVector, blockKey: Mr0kBlockKey, postKey: ToTKey));
            Games.Add(index++, new Game(GameType.Naraka, "Naraka"));
            Games.Add(index++, new Game(GameType.EnsembleStars, "Ensemble Stars"));
            Games.Add(index++, new Game(GameType.OPFP, "OPFP"));
            Games.Add(index++, new Game(GameType.FakeHeader, "Fake Header", GameCategory.Unity));
            Games.Add(index++, new Game(GameType.FantasyOfWind, "Fantasy of Wind"));
            Games.Add(index++, new Game(GameType.ShiningNikki, "Shining Nikki"));
            Games.Add(index++, new Game(GameType.HelixWaltz2, "Helix Waltz 2"));
            Games.Add(index++, new Game(GameType.NetEase, "Net Ease"));
            Games.Add(index++, new Game(GameType.AnchorPanic, "Anchor Panic"));
            Games.Add(index++, new Game(GameType.DreamscapeAlbireo, "Dreamscape Albireo"));
            Games.Add(index++, new Game(GameType.ImaginaryFest, "Imaginary Fest"));
            Games.Add(index++, new Game(GameType.AliceGearAegis, "Alice Gears Aegis"));
            Games.Add(index++, new Game(GameType.ProjectSekai, "Project Sekai"));
            Games.Add(index++, new Game(GameType.CodenameJump, "Codename Jump"));
            Games.Add(index++, new Game(GameType.GirlsFrontline, "Girls Frontline"));
            Games.Add(index++, new Game(GameType.Reverse1999, "Reverse: 1999"));
            Games.Add(index++, new Game(GameType.ArknightsEndfield, "Arknights Endfield"));
            Games.Add(index++, new Game(GameType.ArknightsEndfieldCB3, "Arknights Endfield CBT3"));
            Games.Add(index++, new Game(GameType.ArknightsEndfieldCB2, "Arknights Endfield CBT2"));
            Games.Add(index++, new Game(GameType.ArknightsEndfieldCB1, "Arknights Endfield CBT1"));
            Games.Add(index++, new Game(GameType.Arknights, "Arknights"));
            Games.Add(index++, new Game(GameType.JJKPhantomParade, "JJK Phantom Parade"));
            Games.Add(index++, new Game(GameType.MuvLuvDimensions, "Muv-Luv Dimensions"));
            Games.Add(index++, new Game(GameType.PartyAnimals, "Party Animals"));
            Games.Add(index++, new Game(GameType.LoveAndDeepspace, "Love and Deepspace"));
            Games.Add(index++, new Game(GameType.SchoolGirlStrikers, "Schoolgirl Strikers"));
            Games.Add(index++, new Game(GameType.ExAstris, "ExAstris"));
            Games.Add(index++, new Game(GameType.PerpetualNovelty, "Perpetual Novelty"));
            Games.Add(index++, new Game(GameType.RewindingCadence, "Rewinding Cadence"));
            Games.Add(index++, new Game(GameType.AzurPromiliaCBT2, "Azur Promilia CBT2"));
            
            // unity cn
            var list = UnityCNManager.ReadJson();

            foreach (var entry in list)
            {
                string enumName = entry[0];
                string name = entry[1];
                string key = entry[2];

                GameType type = Enum.TryParse(enumName, out GameType parsed) ? parsed : GameType.UnityCNCustomKey;

                Games.Add(index++, new UnityCNGame(type, new(name, key), GameCategory.Other));
            }

            Games.Add(index++, new UnityCNGame(GameType.UnityCNCustomKey, new("UnityCN Custom Key", ""), GameCategory.Unity));
        }
        public static Game GetGameByType(GameType gameType) => Games.FirstOrDefault(x => x.Value.Type == gameType).Value;
        public static Game GetGame(GameType gameType) => GetGame((int)gameType);
        public static Game GetGame(int index)
        {
            if (!Games.TryGetValue(index, out var format))
            {
                throw new ArgumentException("Invalid format !!");
            }

            return format;
        }

        public static Game GetGame(string name) => Games.FirstOrDefault(x => x.Value.Name == name).Value;
        public static Game GetGameByDisplayName(string displayName) => Games.FirstOrDefault(x => x.Value.DisplayName == displayName).Value;
        public static int GetGameIndex(Game game) => Games.FirstOrDefault(x => x.Value == game).Key;
        public static Game[] GetGames() => Games.Values.ToArray();
        public static string[] GetGameNames() => Games.Values.Select(x => x.Name).ToArray();
        public static string SupportedGames() => $"Supported Games:\n{string.Join("\n", Games.Values.Select(x => x.Name))}";
    }

    public record Game
    {
        public string Name { get; set; }
        public GameType Type { get; }
        public string DisplayName { get; set; }
        public GameCategory Category { get; set; }

        public Game(GameType type, string displayName, GameCategory category = GameCategory.Other)
        {
            Name = type.ToString();
            Type = type;
            DisplayName = displayName;
            Category = category;
        }

        public sealed override string ToString() => Name;
        public string ToDisplayString() => DisplayName;
    }

    public record UnityCNGame : Game
    {
        public UnityCN.Entry Key { get; set; }

        public UnityCNGame(GameType type, UnityCN.Entry key, GameCategory category = GameCategory.Other) : base(type, key.Name, category)
        {
            Key = key;
        }
    }

    public record Mr0k : Game
    {
        public byte[] ExpansionKey { get; }
        public byte[] SBox { get; }
        public byte[] InitVector { get; }
        public byte[] BlockKey { get; }
        public byte[] PostKey { get; }

        public Mr0k(GameType type, string displayName, byte[] expansionKey = null, byte[] sBox = null, byte[] initVector = null, byte[] blockKey = null, byte[] postKey = null) : base(type, displayName, GameCategory.Hoyo)
        {
            ExpansionKey = expansionKey ?? Array.Empty<byte>();
            SBox = sBox ?? Array.Empty<byte>();
            InitVector = initVector ?? Array.Empty<byte>();
            BlockKey = blockKey ?? Array.Empty<byte>();
            PostKey = postKey ?? Array.Empty<byte>();
        }
    }

    public record Blk : Game
    {
        public byte[] ExpansionKey { get; }
        public byte[] SBox { get; }
        public byte[] InitVector { get; }
        public ulong InitSeed { get; }

        public Blk(GameType type, string displayName, byte[] expansionKey = null, byte[] sBox = null, byte[] initVector = null, ulong initSeed = 0, GameCategory category = GameCategory.Hoyo) : base(type, displayName, category)
        {
            ExpansionKey = expansionKey ?? Array.Empty<byte>();
            SBox = sBox ?? Array.Empty<byte>();
            InitVector = initVector ?? Array.Empty<byte>();
            InitSeed = initSeed;
        }
    }

    public record Mhy : Blk
    {
        public byte[] MhyShiftRow { get; }
        public byte[] MhyKey { get; }
        public byte[] MhyMul { get; }

        public Mhy(GameType type, string displayName, byte[] mhyShiftRow, byte[] mhyKey, byte[] mhyMul, byte[] expansionKey = null, byte[] sBox = null, byte[] initVector = null, ulong initSeed = 0) : base(type, displayName, expansionKey, sBox, initVector, initSeed)
        {
            MhyShiftRow = mhyShiftRow;
            MhyKey = mhyKey;
            MhyMul = mhyMul;
        }
    }

    // The int value of every member is PERSISTED as key 0 of an asset map (.map/.json)
    // and is used to pick the game when such a map is loaded. Existing values must NEVER
    // be re-assigned or re-ordered -- doing so silently makes old maps select the wrong
    // game (wrong decryption keys, garbage reads). Add new games at the END only.
    public enum GameType
    {
        Normal = 0,
        UnityCN = 1,
        GI = 2,
        GI_Pack = 3,
        GI_CB1 = 4,
        GI_CB2 = 5,
        GI_CB3 = 6,
        GI_CB3Pre = 7,
        BH3 = 8,
        BH3Pre = 9,
        BH3PrePre = 10,
        SR = 11,
        SR_CB2 = 12,
        ZZZ = 13,
        ZZZ_CB1 = 14,
        ZZZ_CB2 = 15,
        HNA_CB1 = 16,
        HYG_CB1 = 17,
        TOT = 18,
        Naraka = 19,
        EnsembleStars = 20,
        OPFP = 21,
        FakeHeader = 22,
        FantasyOfWind = 23,
        ShiningNikki = 24,
        HelixWaltz2 = 25,
        NetEase = 26,
        AnchorPanic = 27,
        DreamscapeAlbireo = 28,
        ImaginaryFest = 29,
        AliceGearAegis = 30,
        ProjectSekai = 31,
        CodenameJump = 32,
        GirlsFrontline = 33,
        Reverse1999 = 34,
        ArknightsEndfield = 35,
        ArknightsEndfieldCB3 = 36,
        ArknightsEndfieldCB2 = 37,
        ArknightsEndfieldCB1 = 38,
        Arknights = 39,
        JJKPhantomParade = 40,
        MuvLuvDimensions = 41,
        PartyAnimals = 42,
        LoveAndDeepspace = 43,
        SchoolGirlStrikers = 44,
        ExAstris = 45,
        PerpetualNovelty = 46,
        PGR_GLB_KR = 47,
        PGR_CN_JP_TW = 48,
        Archeland_KalpaOfUniverse = 49,
        Archeland_1114 = 50,
        NeuralCloud = 51,
        NeuralCloudCN = 52,
        HiganEruthyll = 53,
        WhiteCord = 54,
        Mecharashi = 55,
        CastlevaniaMoonNightFantasy = 56,
        HYSXZY = 57,
        DoulaContinent = 58,
        BlessGlobal = 59,
        Starside = 60,
        ResonanceSoltice = 61,
        OblivionOverride = 62,
        Dawnlands = 63,
        BB = 64,
        DynastyLegends2 = 65,
        EvernightCN = 66,
        XintianlongBabu = 67,
        FrostpunkBeyondTheIce = 68,
        CatFantasy = 69,
        UnityCNCustomKey = 70,
        RewindingCadence = 71,
        AzurPromiliaCBT2 = 72,
    }


    public enum GameCategory
    {
        Hoyo,
        Other,
        Unity,
        Hidden,
    }

    public static class GameTypes
    {
        public static bool IsNormal(this GameType type) => type == GameType.Normal;
        public static bool IsUnityCN(this Game game) => game is UnityCNGame || game.Type == GameType.UnityCN;
        public static bool IsGI(this GameType type) => type == GameType.GI;
        public static bool IsGIPack(this GameType type) => type == GameType.GI_Pack;
        public static bool IsGICB1(this GameType type) => type == GameType.GI_CB1;
        public static bool IsGICB2(this GameType type) => type == GameType.GI_CB2;
        public static bool IsGICB3(this GameType type) => type == GameType.GI_CB3;
        public static bool IsGICB3Pre(this GameType type) => type == GameType.GI_CB3Pre;
        public static bool IsBH3(this GameType type) => type == GameType.BH3;
        public static bool IsBH3Pre(this GameType type) => type == GameType.BH3Pre;
        public static bool IsBH3PrePre(this GameType type) => type == GameType.BH3PrePre;
        public static bool IsZZZCB1(this GameType type) => type == GameType.ZZZ_CB1;
        public static bool IsZZZCB2(this GameType type) => type == GameType.ZZZ_CB2;
        public static bool IsZZZ(this GameType type) => type == GameType.ZZZ;
        public static bool IsSRCB2(this GameType type) => type == GameType.SR_CB2;
        public static bool IsSR(this GameType type) => type == GameType.SR;
        public static bool IsHNACB1(this GameType type) => type == GameType.HNA_CB1;
        public static bool IsHYGCB1(this GameType type) => type == GameType.HYG_CB1;
        public static bool IsTOT(this GameType type) => type == GameType.TOT;
        public static bool IsNaraka(this GameType type) => type == GameType.Naraka;
        public static bool IsOPFP(this GameType type) => type == GameType.OPFP;
        public static bool IsNetEase(this GameType type) => type == GameType.NetEase;
        public static bool IsArknightsEndfield(this GameType type) => type == GameType.ArknightsEndfield;
        public static bool IsArknightsEndfieldCB3(this GameType type) => type == GameType.ArknightsEndfieldCB3;
        public static bool IsArknightsEndfieldCB2(this GameType type) => type == GameType.ArknightsEndfieldCB2;
        public static bool IsArknightsEndfieldCB1(this GameType type) => type == GameType.ArknightsEndfieldCB1;
        public static bool IsArknights(this GameType type) => type == GameType.Arknights;
        public static bool IsLoveAndDeepspace(this GameType type) => type == GameType.LoveAndDeepspace;
        public static bool IsExAstris(this GameType type) => type == GameType.ExAstris;
        public static bool IsPerpetualNovelty(this GameType type) => type == GameType.PerpetualNovelty;
        public static bool IsRewindingCadence(this GameType type) => type == GameType.RewindingCadence;
        public static bool IsAzurPromiliaCBT2(this GameType type) => type == GameType.AzurPromiliaCBT2;
        public static bool IsGIGroup(this GameType type) => type switch
        {
            GameType.GI or GameType.GI_Pack or GameType.GI_CB1 or GameType.GI_CB2 or GameType.GI_CB3 or GameType.GI_CB3Pre => true,
            _ => false,
        };

        public static bool IsZZZGroup(this GameType type) => type switch
        {
            GameType.ZZZ or GameType.ZZZ_CB1 or GameType.ZZZ_CB2 => true,
            _ => false,
        };

        public static bool IsGISubGroup(this GameType type) => type switch
        {
            GameType.GI or GameType.GI_CB2 or GameType.GI_CB3 or GameType.GI_CB3Pre => true,
            _ => false,
        };

        public static bool IsBH3Group(this GameType type) => type switch
        {
            GameType.BH3 or GameType.BH3Pre => true,
            _ => false,
        };

        public static bool IsSRGroup(this GameType type) => type switch
        {
            GameType.SR_CB2 or GameType.SR => true,
            _ => false,
        };

        public static bool IsBlockFile(this GameType type) => type switch
        {
            GameType.BH3 or GameType.BH3Pre or GameType.ZZZ_CB2 or GameType.ZZZ or GameType.SR or GameType.GI_Pack or GameType.TOT or GameType.ArknightsEndfieldCB2 => true,
            _ => false,
        };

        public static bool IsMhyGroup(this GameType type) => type switch
        {
            GameType.GI or GameType.GI_Pack or GameType.GI_CB1 or GameType.GI_CB2 or GameType.GI_CB3 or GameType.GI_CB3Pre or GameType.BH3 or GameType.BH3Pre or GameType.BH3PrePre or GameType.SR_CB2 or GameType.SR or GameType.ZZZ_CB1 or GameType.ZZZ_CB2 or GameType.ZZZ or GameType.HYG_CB1 or GameType.TOT => true,
            _ => false,
        };

        public static bool IsArknightsEndfieldGroup(this GameType type) => type switch
        {
            GameType.ArknightsEndfieldCB2 or GameType.ArknightsEndfieldCB3 or GameType.ArknightsEndfield => true,
            _ => false,
        };
    }
}

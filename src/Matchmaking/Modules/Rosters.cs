using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SS.Core.ComponentInterfaces;
using System.IO;
using SS.Core;
using System.Data.Common;
using SS.Core.Modules;

namespace SS.Matchmaking.Modules
{
    public interface IRosters : IComponentInterface
    {
        public void AddPlayer(String playerName, String squadName, Boolean isACaptain);
        public void RemovePlayer(String playerName);
        public Dictionary<String, Boolean> GetRoster(String squadName);
        public List<String> GetSquads();
    }

    internal class Rosters : IModule, IRosters
    {
        #region Properties
        String ConnectionString { get; } = $"Data Source=./data/SS.Core.Modules.PersistSQLite.db;Foreign Keys=True;Pooling=True";
        SqliteConnection Connection { get; }
        private IChat Chat { get; }
        private ICommandManager CommandManager { get; }
        private InterfaceRegistrationToken<IRosters>? IRostersToken { get; set; }
        #endregion Properties

        #region Constructor
        public Rosters(ICommandManager commandManager, IChat chat)
        {
            CommandManager = commandManager;
            Chat = chat;

            CommandManager.AddCommand("addplayer", Command_AddPlayer);
            CommandManager.AddCommand("removeplayer", Command_RemovePlayer);
            CommandManager.AddCommand("getroster", Command_GetRoster);
            CommandManager.AddCommand("getsquads", Command_GetSquads);

            Connection = new(ConnectionString);
            Connection.Open();

            CreateRostersTableIfNotExists();
        }
        #endregion Constructor

        #region IModule
        public bool Load(IComponentBroker broker)
        {
            IRostersToken = broker.RegisterInterface<IRosters>(this);
            return true;
        }

        public bool Unload(IComponentBroker broker)
        {
            InterfaceRegistrationToken<IRosters>? iRostersToken = IRostersToken;
            if (broker.UnregisterInterface(ref iRostersToken) != 0)
                return false;

            return true;
        }
        #endregion IModule

        #region Commands
        public void Command_AddPlayer(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            try
            {
                String[] paramsin = parameters.ToString().Split(',');
                String playerName = paramsin[0];
                String squadName = paramsin[1];
                Boolean isACaptain;

                if (paramsin.Count() == 3)
                    isACaptain = Convert.ToBoolean(paramsin[2]);
                else
                    isACaptain = false;

                AddPlayer(playerName, squadName, isACaptain);
            }
            catch
            {
                try
                {
                    Chat.SendMessage(player, $"An error occurred in the addplayer command. The command should be: ?addplayer player,squad,<cap> where <cap> is either true or false, indicating whether player is a captain.");
                }
                catch { }
            }
        }

        public void Command_RemovePlayer(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            try
            {
                String playerName = parameters.ToString();
                RemovePlayer(playerName);
            }
            catch (Exception e)
            {
                try
                {
                    Chat.SendMessage(player, e.Message);
                }
                catch { }
            }
        }

        public void Command_GetRoster(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            try
            {
                String squadName = parameters.ToString();
                Dictionary<String, Boolean> roster = GetRoster(squadName);

                Chat.SendMessage(player, $"Roster for {squadName}:");
                Chat.SendMessage(player, $"PlayerName            Captain");

                foreach (var playerEntry in roster)
                {
                    Chat.SendMessage(player, $"{playerEntry.Key:20} {playerEntry.Value:5}");
                }
            }
            catch (Exception e)
            {
                try
                {
                    Chat.SendMessage(player, e.Message);
                }
                catch { }
            }
        }

        public void Command_GetSquads(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            try
            {
                List<String> squadList = GetSquads();

                Chat.SendMessage(player, $"Full list of squads:");
                foreach (var squadName in squadList)
                {
                    Chat.SendMessage(player, squadName);
                }
            }
            catch (Exception e)
            {
                try
                {
                    Chat.SendMessage(player, e.Message);
                }
                catch { }
            }
        }
        #endregion Commands

        #region Helper methods
        public Boolean DoesTableExist()
        {
            var command = Connection.CreateCommand();

            command.CommandText =
                "SELECT EXISTS " +
                "(SELECT FROM information_schema.tables " +
                "WHERE table_schema = 'public' " +
                "AND table_name = 'rosters')";

            using (var reader = command.ExecuteReader())
            {
                return reader.GetBoolean(0);
            }
        }

        public void CreateRostersTableIfNotExists()
        {
            if (DoesTableExist())
                return;

            SqliteCommand command = Connection.CreateCommand();

            command.CommandText = @"-- Table: public.rosters

-- DROP TABLE IF EXISTS public.rosters;

CREATE TABLE IF NOT EXISTS public.rosters
(
    ""Rosters_PK"" integer NOT NULL GENERATED ALWAYS AS IDENTITY ( INCREMENT 1 START 1 MINVALUE 1 MAXVALUE 2147483647 CACHE 1 ),
    ""PlayerName"" character varying(20) COLLATE pg_catalog.""default"" NOT NULL,
    ""SquadName"" character varying(20) COLLATE pg_catalog.""default"" NOT NULL,
    ""Captain"" boolean NOT NULL,
    CONSTRAINT ""Rosters_PK"" PRIMARY KEY (""Rosters_PK"")
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.rosters
    OWNER to postgres;";

            command.ExecuteNonQuery();
        }

        public void AddPlayer(string playerName, string squadName, bool isACaptain)
        {
            if (PlayerExists(playerName))
                return;

            SqliteCommand command = Connection.CreateCommand();

            command.CommandText =
                "insert into public.rosters " +
                "(\"PlayerName\", \"SquadName\", \"Captain\") " +
                "values ($playerName, $squadName, $isACaptain);";
            command.Parameters.AddWithValue("$playerName", playerName);
            command.Parameters.AddWithValue("$squadName", squadName);
            command.Parameters.AddWithValue("$isACaptain", isACaptain);

            command.ExecuteNonQuery();
        }

        public void RemovePlayer(string playerName)
        {
            SqliteCommand command = Connection.CreateCommand();

            command.CommandText =
                "delete from public.\"rosters\" r where r.\"PlayerName\" = $playerName;";
            command.Parameters.AddWithValue("$playerName", playerName);

            command.ExecuteNonQuery();
        }

        public Dictionary<String, Boolean> GetRoster(string squadName)
        {
            Dictionary<String, Boolean> rosterData = new();

            var command = Connection.CreateCommand();

            command.CommandText =
                "select * from public.\"rosters\" r " +
                "where r.\"SquadName\" = $squadName;";
            command.Parameters.AddWithValue("$squadName", squadName);

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rosterData.Add(reader.GetString(1), reader.GetBoolean(3));
                }

                return rosterData;
            }
        }

        public List<string> GetSquads()
        {
            List<String> squads = new();

            var command = Connection.CreateCommand();

            command.CommandText =
                "select distinct \"SquadName\" from public.rosters";

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    squads.Add(reader.GetString(0));
                }

                return squads;
            }
        }

        private Boolean PlayerExists(String playerName)
        {
            var command = Connection.CreateCommand();

            command.CommandText =
                "select * from public.\"rosters\" r " +
                "where r.\"PlayerName\" = $playerName;";
            command.Parameters.AddWithValue("$playerName", playerName);

            using (var reader = command.ExecuteReader())
            {
                return reader.Read();
            }
        }
        #endregion Helper methods
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SS.Core.ComponentInterfaces;
using System.IO;
using SS.Core;
using System.Data.Common;
using SS.Core.Modules;
using Npgsql;
using System.Numerics;

namespace SS.Matchmaking.Modules
{
    public interface IRosters : IComponentInterface
    {
        public void AddPlayer(String playerName, String squadName, Boolean isACaptain);
        public void RemovePlayer(String playerName);
        public Task<Dictionary<String, Boolean>> GetRoster(String squadName);
        public Task<List<String>> GetSquads();
    }

    internal class Rosters : IAsyncModule, IRosters, IDisposable
    {
        #region Properties
        String ConnectionString { get; set; }
        private NpgsqlDataSource? DataSource { get; set; }
        private IChat Chat { get; }
        private ICommandManager CommandManager { get; }
        private InterfaceRegistrationToken<IRosters>? IRostersToken { get; set; }
        private IConfigManager ConfigManager { get; }
        #endregion Properties

        #region Constructor
        public Rosters(ICommandManager commandManager, IChat chat, IConfigManager configManager)
        {
            CommandManager = commandManager;
            Chat = chat;
            ConfigManager = configManager;

            CommandManager.AddCommand("addplayer", Command_AddPlayer);
            CommandManager.AddCommand("removeplayer", Command_RemovePlayer);
            CommandManager.AddCommand("getroster", Command_GetRoster);
            CommandManager.AddCommand("getsquads", Command_GetSquads);

            ConnectionString = ConfigManager.GetStr(ConfigManager.Global, "SS.Matchmaking", "DatabaseConnectionString");
            DataSource = NpgsqlDataSource.Create(ConnectionString);

            Task.Run(() => this.CreateRostersTableIfNotExists()).Wait();
            ConfigManager = configManager;
        }
        #endregion Constructor

        #region IAsyncModule
        public async Task<bool> LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            IRostersToken = broker.RegisterInterface<IRosters>(this);
            return true;
        }

        public async Task<bool> UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            InterfaceRegistrationToken<IRosters>? iRostersToken = IRostersToken;
            if (broker.UnregisterInterface(ref iRostersToken) != 0)
                return false;

            return true;
        }
        #endregion IAsyncModule

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
            String squadName = parameters.ToString();
            GetRosterIntermediary(player,squadName);
        }

        public void Command_GetSquads(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            GetSquadsIntermediary(player);
        }
        #endregion Commands

        #region Helper methods
        public async void GetSquadsIntermediary(Player playerToReply)
        {
            try
            {
                List<String> squadList = await GetSquads();

                Chat.SendMessage(playerToReply, $"Full list of squads:");
                foreach (var squadName in squadList)
                {
                    Chat.SendMessage(playerToReply, squadName);
                }
            }
            catch (Exception e)
            {
                try
                {
                    Chat.SendMessage(playerToReply, e.Message);
                }
                catch { }
            }
        }

        public async void GetRosterIntermediary(Player playerToReply, String squadName)
        {
            try
            {
                Dictionary<String, Boolean> roster = await GetRoster(squadName);

                Chat.SendMessage(playerToReply, $"Roster for {squadName}:");
                Chat.SendMessage(playerToReply, $"PlayerName            Captain");

                foreach (var playerEntry in roster)
                {
                    Chat.SendMessage(playerToReply, $"{playerEntry.Key:20} {playerEntry.Value:5}");
                }
            }
            catch (Exception e)
            {
                try
                {
                    Chat.SendMessage(playerToReply, e.Message);
                }
                catch { }
            }
        }

        public async Task<Boolean> DoesTableExist()
        {
            await using var dataSource = NpgsqlDataSource.Create(ConnectionString);

            await using (var cmd = dataSource.CreateCommand("SELECT EXISTS " +
                "(SELECT FROM information_schema.tables " +
                "WHERE table_schema = 'public' " +
                "AND table_name = 'rosters')"))
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    return reader.GetBoolean(0);
                }
            }

            return false;
        }

        public async void CreateRostersTableIfNotExists()
        {
            if (await DoesTableExist())
                return;

            // Insert some data
            await using (var command = DataSource.CreateCommand(
                @"-- Table: public.rosters

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
    OWNER to postgres;"
                ))
            {
                await command.ExecuteNonQueryAsync();
            }
        }

        public async void AddPlayer(string playerName, string squadName, bool isACaptain)
        {
            if (await PlayerExists(playerName))
                return;

            await using (var command = DataSource.CreateCommand(
                "insert into public.rosters " +
                "(\"PlayerName\", \"SquadName\", \"Captain\") " +
                "values ($1, $2, $3);"
                ))
            {
                command.Parameters.AddWithValue(playerName);
                command.Parameters.AddWithValue(squadName);
                command.Parameters.AddWithValue(isACaptain);

                await command.ExecuteNonQueryAsync();
            }
        }

        public async void RemovePlayer(string playerName)
        {
            await using (var command = DataSource.CreateCommand(
                "delete from public.\"rosters\" r where r.\"PlayerName\" = $1;"
                ))
            {
                command.Parameters.AddWithValue(playerName);

                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task<Dictionary<String, Boolean>> GetRoster(string squadName)
        {
            Dictionary<String, Boolean> rosterData = new();

            // Retrieve all rows
            await using var command = DataSource.CreateCommand(
                "select * from public.\"rosters\" r " +
                "where r.\"SquadName\" = $1;"
                );
            command.Parameters.AddWithValue(squadName);

            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rosterData.Add(reader.GetString(1), reader.GetBoolean(3));
                }
            }

            return rosterData;
        }

        public async Task<List<String>> GetSquads()
        {
            List<String> squads = new();

            // Retrieve all rows
            await using var command = DataSource.CreateCommand(
                "select distinct \"SquadName\" from public.rosters"
                );

            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    squads.Add(reader.GetString(0));
                }
            }

            return squads;
        }

        private async Task<Boolean> PlayerExists(String playerName)
        {
            await using var command = DataSource.CreateCommand(
                "select * from public.\"rosters\" r " +
                "where r.\"PlayerName\" = $1;"
                );
            command.Parameters.AddWithValue(playerName);

            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    return reader.Read();
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (DataSource != null)
                DataSource.Dispose();
        }
        #endregion Helper methods
    }
}

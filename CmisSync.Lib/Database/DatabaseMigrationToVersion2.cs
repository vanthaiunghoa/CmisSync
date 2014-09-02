﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
#if __MonoCS__
using Mono.Data.Sqlite;
#else
using System.Data.SQLite;
#endif

using log4net;
using CmisSync.Auth;

namespace CmisSync.Lib.Database
{
    #if __MonoCS__
    // Mono's SQLite ADO implementation uses pure CamelCase (Sqlite vs. SQLite)
    // so we define some aliases here
    using SQLiteConnection = SqliteConnection;
    using SQLiteCommand = SqliteCommand;
    using SQLiteException = SqliteException;
    using SQLiteDataReader = SqliteDataReader;
    #endif

    /// <summary>
    /// Migrate from database version 0 to version 2.
    /// </summary>
    public class DatabaseMigrationToVersion2 : DatabaseMigrationBase
    {
        /// <summary>
        /// Log.
        /// </summary>
        private static readonly ILog Logger = LogManager.GetLogger(typeof(DatabaseMigrationToVersion2));

        /// <summary>
        /// Migrate from database version 0 to version 2.
        /// </summary>
        /// <param name="filePath">File path.</param>
        /// <param name="connection">Connection.</param>
        /// <param name="currentVersion">Current database schema version.</param>
        public void Migrate(Config.SyncConfig.Folder syncFolder, SQLiteConnection connection, int currentVersion)
        {
            // Add columns and other database schema manipulation.
            MigrateSchema(syncFolder, connection);

            // Fill the data which is missing due to new columns in the database.
            FillMissingData(syncFolder, connection);

            // If everything has succeded, upgrade database version number.
            SetDatabaseVersion(connection, currentVersion);
        }


        /// <summary>
        /// Add columns and other database schema manipulation.
        /// </summary>
        /// <param name="dbFilePath">Db file path.</param>
        /// <param name="folderName">Folder name.</param>
        public static void MigrateSchema(Config.SyncConfig.Folder syncFolder, SQLiteConnection connection)
        {
            // Add columns
            var filesTableColumns = GetColumnNames(connection, "files");
            if (!filesTableColumns.Contains("localPath"))
            {
                ExecuteSQLAction(connection,
                    @"ALTER TABLE files ADD COLUMN localPath TEXT;", null);
            }
            if (!filesTableColumns.Contains("id"))
            {
                ExecuteSQLAction(connection,
                    @"ALTER TABLE files ADD COLUMN id TEXT;", null);
            }

            var foldersTableColumns = GetColumnNames(connection, "folders");
            if (!foldersTableColumns.Contains("localPath"))
            {
                ExecuteSQLAction(connection,
                    @"ALTER TABLE folders ADD COLUMN localPath TEXT;", null);
            }
            if (!foldersTableColumns.Contains("id"))
            {
                ExecuteSQLAction(connection,
                    @"ALTER TABLE folders ADD COLUMN id TEXT;", null);
            }

            // Create indices
            ExecuteSQLAction(connection,
                @"CREATE INDEX IF NOT EXISTS files_localPath_index ON files (localPath);
                  CREATE INDEX IF NOT EXISTS files_id_index ON files (id);
                  CREATE INDEX IF NOT EXISTS folders_localPath_index ON folders (localPath);
                  CREATE INDEX IF NOT EXISTS folders_id_index ON folders (id);", null);

            // Create tables
            ExecuteSQLAction(connection,
                @"CREATE TABLE IF NOT EXISTS downloads (
                    PATH TEXT PRIMARY KEY,
                    serverSideModificationDate DATE);     /* Download */
                CREATE TABLE IF NOT EXISTS failedoperations (
                    path TEXT PRIMARY KEY,
                    lastLocalModificationDate DATE,
                    uploadCounter INTEGER,
                    downloadCounter INTEGER,
                    changeCounter INTEGER,
                    deleteCounter INTEGER,
                    uploadMessage TEXT,
                    downloadMessage TEXT,
                    changeMessage TEXT,
                    deleteMessage TEXT);",
                null);
        }


        /// <summary>
        /// Fill the data which is missing due to new columns in the database.
        /// </summary>
        /// <param name="dbFilePath">Db file path.</param>
        /// <param name="folderName">Folder name.</param>
        public static void FillMissingData(Config.SyncConfig.Folder syncFolder, SQLiteConnection connection)
        {
            Utils.NotifyUser("CmisSync needs to upgrade its own local data for folder \"" + syncFolder.RepositoryId + "\". Please stay on the network for a few minutes.");

            var session = Auth.Auth.GetCmisSession(
                              ((Uri)syncFolder.RemoteUrl).ToString(),
                              syncFolder.UserName,
                              Crypto.Deobfuscate(syncFolder.ObfuscatedPassword),
                              syncFolder.RepositoryId);

            var filters = new HashSet<string>();
            filters.Add("cmis:objectId");
            //session.DefaultContext = session.CreateOperationContext(filters, false, true, false, DotCMIS.Enums.IncludeRelationshipsFlag.None, null, true, null, true, 100);
            string remoteRootFolder = syncFolder.RemotePath;
            string localRootFolder = syncFolder.LocalPath.Substring(ConfigManager.CurrentConfig.FoldersPath.Length + 1);

            try
            {
                using (var command = new SQLiteCommand(connection))
                {
                    // Fill missing columns of all files.
                    command.CommandText = "SELECT path FROM files WHERE id IS NULL;";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            // Example: "old-db-1.0.13/テスト・テスト/テスト用ファイル.pptx"
                            string legacyPath = reader["path"].ToString();

                            // Example: /Sites/cmissync/documentLibrary/tests/テスト・テスト/テスト用ファイル.pptx
                            string remotePath = remoteRootFolder + legacyPath.Substring(localRootFolder.Length);

                            // Example: テスト・テスト/テスト用ファイル.pptx
                            string localPath = legacyPath.Substring(localRootFolder.Length + 1);

                            string id = null;
                            try
                            {
                                id = session.GetObjectByPath(remotePath).Id;
                            }
                            catch (DotCMIS.Exceptions.CmisObjectNotFoundException e)
                            {
                                Logger.Info(String.Format("File Not Found: \"{0}\"", remotePath), e);
                            }
                            catch (DotCMIS.Exceptions.CmisPermissionDeniedException e)
                            {
                                Logger.Info(String.Format("PermissionDenied: \"{0}\"", remotePath), e);
                            }

                            var parameters = new Dictionary<string, object>();
                            parameters.Add("@id", id);
                            parameters.Add("@localPath", localPath);
                            parameters.Add("@path", legacyPath);
                            ExecuteSQLAction(connection, "UPDATE files SET id = @id, localPath = @localPath WHERE path = @path;", parameters);
                        }
                    }

                    // Fill missing columns of all folders.
                    command.CommandText = "SELECT path FROM folders WHERE id IS NULL;";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string legacyPath = reader["path"].ToString();
                            string remotePath = remoteRootFolder + legacyPath.Substring(localRootFolder.Length);
                            string localPath = legacyPath.Substring(localRootFolder.Length + 1);
                            string id = null;
                            try
                            {
                                id = session.GetObjectByPath(remotePath).Id;
                            }
                            catch (DotCMIS.Exceptions.CmisObjectNotFoundException e)
                            {
                                Logger.Info(String.Format("File Not Found: \"{0}\"", remotePath), e);
                            }
                            catch (DotCMIS.Exceptions.CmisPermissionDeniedException e)
                            {
                                Logger.Info(String.Format("PermissionDenied: \"{0}\"", remotePath), e);
                            }

                            var parameters = new Dictionary<string, object>();
                            parameters.Add("@id", id);
                            parameters.Add("@localPath", localPath);
                            parameters.Add("@path", legacyPath);
                            ExecuteSQLAction(connection, "UPDATE folders SET id = @id, localPath = @localPath WHERE path = @path;", parameters);
                        }
                    }

                    {
                        // Replace repository path prefix.
                        // Before: C:\Users\myuser\CmisSync
                        // After:  C:\Users\myuser\CmisSync\myfolder

                        // Read existing prefix.

                        string newPrefix = syncFolder.LocalPath;

                        var parameters = new Dictionary<string, object>();
                        parameters.Add("prefix", newPrefix);
                        ExecuteSQLAction(connection, "INSERT OR REPLACE INTO general (key, value) VALUES (\"PathPrefix\", @prefix)", parameters);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Info("Failed to fills object id.", e);
                throw;
            }

            Utils.NotifyUser("CmisSync has finished upgrading its own local data for folder \"" + syncFolder.RepositoryId + "\".");
        }
    }
}
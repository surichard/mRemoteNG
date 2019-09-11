﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using mRemoteNG.Config.DatabaseConnectors;
using mRemoteNG.Config.DataProviders;
using mRemoteNG.Config.Serializers;
using mRemoteNG.Config.Serializers.MsSql;
using mRemoteNG.Config.Serializers.Versioning;
using mRemoteNG.Container;
using mRemoteNG.Security;
using mRemoteNG.Security.Authentication;
using mRemoteNG.Security.SymmetricEncryption;
using mRemoteNG.Tools;
using mRemoteNG.Tree;
using mRemoteNG.Tree.Root;

namespace mRemoteNG.Config.Connections
{
    public class SqlConnectionsLoader : IConnectionsLoader
    {
        private readonly IDeserializer<string, IEnumerable<LocalConnectionPropertiesModel>>
            _localConnectionPropertiesDeserializer;

        private readonly IDataProvider<string> _dataProvider;
        private Boolean _isDatabaseRecheable = false;

        public Func<Optional<SecureString>> AuthenticationRequestor { get; set; } =
            () => MiscTools.PasswordDialog("", false);

        public SqlConnectionsLoader(
            IDeserializer<string, IEnumerable<LocalConnectionPropertiesModel>> localConnectionPropertiesDeserializer,
            IDataProvider<string> dataProvider)
        {
            _localConnectionPropertiesDeserializer =
                localConnectionPropertiesDeserializer.ThrowIfNull(nameof(localConnectionPropertiesDeserializer));
            _dataProvider = dataProvider.ThrowIfNull(nameof(dataProvider));
        }

        public ConnectionTreeModel Load()
        {
            try
            {
                var connector = DatabaseConnectorFactory.DatabaseConnectorFromSettings();
                var dataProvider = new SqlDataProvider(connector);
                var metaDataRetriever = new SqlDatabaseMetaDataRetriever();
                var databaseVersionVerifier = new SqlDatabaseVersionVerifier(connector);
                var cryptoProvider = new LegacyRijndaelCryptographyProvider();

                var metaData = metaDataRetriever.GetDatabaseMetaData(connector) ??
                               HandleFirstRun(metaDataRetriever, connector);
                var decryptionKey = GetDecryptionKey(metaData);

                if (!decryptionKey.Any())
                    throw new Exception("Could not load SQL connections");

                databaseVersionVerifier.VerifyDatabaseVersion(metaData.ConfVersion);
                var dataTable = dataProvider.Load();
                var deserializer = new DataTableDeserializer(cryptoProvider, decryptionKey.First());
                var connectionTree = deserializer.Deserialize(dataTable);
                ApplyLocalConnectionProperties(connectionTree.RootNodes.First(i => i is RootNodeInfo));
                if (metaDataRetriever.IsLocalCacheEnabled(connector))
                {
                    var fileName = GetSQLCacheFilePath();
                    var xmlConnectionSaver = new XmlConnectionsSaver(fileName, new SaveFilter(),false);
                    xmlConnectionSaver.Save(connectionTree);
                }
                _isDatabaseRecheable = true;
                return connectionTree;

            } catch (Exception ex)
            {
                _isDatabaseRecheable = false;
                if (LocalCacheDatabaseFileExists())
                {
                    // Try to use xml cache file
                        var xmlConnectionLoader = new XmlConnectionsLoader(GetSQLCacheFilePath());
                    return xmlConnectionLoader.Load();
                }
                throw ex;
            }
         
        }

        private Optional<SecureString> GetDecryptionKey(SqlConnectionListMetaData metaData)
        {
            var cryptographyProvider = new LegacyRijndaelCryptographyProvider();
            var cipherText = metaData.Protected;
            var authenticator = new PasswordAuthenticator(cryptographyProvider, cipherText, AuthenticationRequestor);
            var authenticated =
                authenticator.Authenticate(new RootNodeInfo(RootNodeType.Connection).DefaultPassword
                                                                                    .ConvertToSecureString());

            if (authenticated)
                return authenticator.LastAuthenticatedPassword;
            return Optional<SecureString>.Empty;
        }

        private void ApplyLocalConnectionProperties(ContainerInfo rootNode)
        {
            var localPropertiesXml = _dataProvider.Load();
            var localConnectionProperties = _localConnectionPropertiesDeserializer.Deserialize(localPropertiesXml);

            rootNode
                .GetRecursiveChildList()
                .Join(localConnectionProperties,
                      con => con.ConstantID,
                      locals => locals.ConnectionId,
                      (con, locals) => new {Connection = con, LocalProperties = locals})
                .ForEach(x =>
                {
                    x.Connection.PleaseConnect = x.LocalProperties.Connected;
                    x.Connection.Favorite = x.LocalProperties.Favorite;
                    if (x.Connection is ContainerInfo container)
                        container.IsExpanded = x.LocalProperties.Expanded;
                });
        }

        private SqlConnectionListMetaData HandleFirstRun(SqlDatabaseMetaDataRetriever metaDataRetriever, IDatabaseConnector connector)
        {
	        metaDataRetriever.WriteDatabaseMetaData(new RootNodeInfo(RootNodeType.Connection), connector);
	        return metaDataRetriever.GetDatabaseMetaData(connector);
		}

        private bool LocalCacheDatabaseFileExists()
        {
            var localCacheFile = GetSQLCacheFilePath();
            return File.Exists(localCacheFile);

            //return mRemoteNG.Settings.Default.SQLCacheDatabaseEntry;
        }

        private String GetSQLCacheFilePath()
        {
            return Path.Combine(Environment.GetFolderPath(
                           Environment.SpecialFolder.ApplicationData), "mRemoteNG/sqlcache.xml");
        }

        public Boolean IsDatabaseRecheable()
        {
            return _isDatabaseRecheable;
        }
    }
}
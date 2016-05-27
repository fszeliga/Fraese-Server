using De.Boenigk.SMC5D.Basics;
using De.Boenigk.SMC5D.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WebServer
{
    public static class ConnectorUtility
    {
        private static Connector instance;
        public static Connector GetConnector()
        {
            if (instance == null)
            {
                instance = new Connector("\\");
                instance.Load("\\config.xml");
            }
            return instance;
        }

        public static SMCSettings GetConnectorSettings()
        {
            return GetConnector().SMCSettings;
        }
    }
}

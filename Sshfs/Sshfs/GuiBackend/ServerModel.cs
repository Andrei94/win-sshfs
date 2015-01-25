﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sshfs.GuiBackend
{
    /// a class to generate Server Objects, that contains a list of Folder Objects.
    public class ServerModel
    {
#region ATTRIBUTES
        public string Name { get; set; }
        public Guid ID{get; set;}

        public string Notes{get; set;}

        public string PrivateKey { get; set; }
        public string Password { get; set; }
        public string Passphrase { get; set; }

        public string Username { get; set; }

        public string Host { get; set; }
        public int Port { get; set; }

        public ConnectionType Type;

        public System.Windows.Forms.TreeNode gui_node = null;

        //public bool Automount { get; set; }

        //public ConnectionType ConnectionType {get; set; }
        //public DriveStatus Status { get; set; }

        //public string Root;
        //public char DriveLetter;

        //Folders for a Server can be stored as List of Foldermodels
        // or as List of Strings
        //public List<FolderModel> Mountpoints = new List<FolderModel>();
        //public List<string> Mountpoint;
        public List<FolderModel>Folders;// = new Dictionary<Guid, FolderModel>();

        public ServerModel()
        {
//            Mountpoints = new List<FolderModel>();
            Folders = new List<FolderModel>();
        }



#endregion

    }
}

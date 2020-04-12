using System;
using System.Linq;
using System.Threading.Tasks;

namespace Sshfs
{
	public static class SftpApplication
	{
		public static void Run(string[] args)
		{
			Task.Factory.StartNew(() => new SftpDrive
			{
				Name = args[0],
				Port = Convert.ToInt32(args[1]),
				MountPoint = string.Format("/sftpg/{0}/data/", args[2]),
				Host = args[3],
				Letter = Utilities.GetAvailableDrives().Last(),
				Root = "/data", //string.Format("/sftpg/{0}/data", args[2]),
				KeepAliveInterval = 30,
				ConnectionType = ConnectionType.Password,
				Username = args[4],
				Password = args[5]
			}.Mount());
		}
	}
}
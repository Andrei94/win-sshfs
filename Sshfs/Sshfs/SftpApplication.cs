using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sshfs
{
	public static class SftpApplication
	{
		public static void Run(string[] args)
		{
			var drive = new SftpDrive
			{
				Name = args[0],
				Port = Convert.ToInt32(args[1]),
				MountPoint = string.Format("/sftpg/{0}/data/", args[2]),
				Host = args[3],
				Letter = Utilities.GetAvailableDrives().Last(),
				Root = "/data",
				KeepAliveInterval = 30,
				ConnectionType = ConnectionType.Password,
				Username = args[4],
				Password = args[5]
			};
			Task.Factory.StartNew(() => {
				do
				{
					drive.Mount();
					Thread.Sleep(TimeSpan.FromSeconds(15));
				}
				while (drive.Status != DriveStatus.Mounted);
			});
			while (true)
			{
				Thread.Sleep(TimeSpan.FromMinutes(30));
				while (drive.Status != DriveStatus.Mounted)
					drive.Mount();
			}
		}
	}
}
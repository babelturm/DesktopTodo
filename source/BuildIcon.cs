using System;
using System.IO;
namespace DesktopTodo
{
    static class BuildIcon
    {
        static void Main(string[] args) { NotificationIcon.WriteIco(args[0]); if(args.Length>1) NotificationIcon.Write(args[1]); }
    }
}

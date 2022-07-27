namespace DaemonKit {

    public class Command {
        public const int SHUTDOWN = 1001;
        public const int RESTART = 1002;
        public const int BOOT = 1003;
        public const int RESTART_NODE_TREE = 1004;
        public int ID = BOOT;

    }

}
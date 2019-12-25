using AT_Utils;
using UnityEngine;

namespace CargoAccelerators
{
    public class ExtensibleMagneticDamper : ATMagneticDamper
    {
        public bool AddDamperExtension(Transform T)
        {
            var sensor = T.GetComponent<MeshFilter>();
            if(sensor == null)
            {
                this.Log($"AddDamperExtension: {T.GetID()} does not have MeshFilter component");
                return false;
            }
            addSensor(sensor);
            return true;
        }

        public void RemoveDamperExtension(Transform T)
        {
            var sensor = T.GetComponent<Sensor>();
            if(sensor == null)
            {
                this.Log($"RemoveDamperExtension: {T.GetID()} does not have Sensor component");
                return;
            }
            sensors.Remove(sensor);
        }
    }
}

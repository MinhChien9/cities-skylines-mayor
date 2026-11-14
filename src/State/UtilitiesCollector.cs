using System.Text;
using ColossalFramework;
using SkylinesAgentBridge.Commands;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    /// <summary>
    /// GET /state/utilities: the city-wide capacity against demand for every service the
    /// info views show as a "capacity / usage" pair: electricity, water, sewage, garbage,
    /// healthcare, deathcare, education, crime, plus jobs and unemployment.
    ///
    /// Everything comes from district 0 (the whole city) through the game's own
    /// <c>District.Get*</c> accessors, the numbers the info panels read. Units are the
    /// game's raw units; the electricity pair is in kW (the panel divides by 1000 for MW).
    /// </summary>
    public static class UtilitiesCollector
    {
        public static CommandResult BuildUtilitiesJson()
        {
            District d = Singleton<DistrictManager>.instance.m_districts.m_buffer[0];

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            json.Append(",\"population\":").Append(d.m_populationData.m_finalCount);

            AppendPair(json, "electricity", "capacity", d.GetElectricityCapacity(), "consumption", d.GetElectricityConsumption());
            AppendPair(json, "water", "capacity", d.GetWaterCapacity(), "consumption", d.GetWaterConsumption());
            AppendPair(json, "sewage", "capacity", d.GetSewageCapacity(), "accumulation", d.GetSewageAccumulation());
            json.Append(",\"waterPollutionPercent\":").Append(d.GetWaterPollution());
            json.Append(",\"waterStorage\":{\"amount\":").Append(d.GetWaterStorageAmount())
                .Append(",\"capacity\":").Append(d.GetWaterStorageCapacity()).Append("}");
            json.Append(",\"heating\":{\"capacity\":").Append(d.GetHeatingCapacity())
                .Append(",\"consumption\":").Append(d.GetHeatingConsumption()).Append("}");

            json.Append(",\"garbage\":{\"storageCapacity\":").Append(d.GetGarbageCapacity())
                .Append(",\"stored\":").Append(d.GetGarbageAmount())
                .Append(",\"incinerationCapacity\":").Append(d.GetIncinerationCapacity())
                .Append(",\"accumulation\":").Append(d.GetGarbageAccumulation())
                .Append(",\"piles\":").Append(d.GetGarbagePiles()).Append("}");

            json.Append(",\"healthcare\":{\"healCapacity\":").Append(d.GetHealCapacity())
                .Append(",\"sick\":").Append(d.GetSickCount())
                .Append(",\"hospitals\":").Append(d.GetHospitalCount())
                .Append(",\"productivityPercent\":").Append(d.GetHealthCareProductivity()).Append("}");

            json.Append(",\"deathcare\":{\"cemeteryCapacity\":").Append(d.GetDeadCapacity())
                .Append(",\"cemeteryStored\":").Append(d.GetDeadAmount())
                .Append(",\"crematoryCapacity\":").Append(d.GetCremateCapacity())
                .Append(",\"dead\":").Append(d.GetDeadCount())
                .Append(",\"averageLifespan\":").Append(d.GetAverageLifespan()).Append("}");

            json.Append(",\"education\":{");
            json.Append("\"elementary\":{\"capacity\":").Append(d.GetEducation1Capacity())
                .Append(",\"need\":").Append(d.GetEducation1Need())
                .Append(",\"ratePercent\":").Append(d.GetEducation1Rate()).Append("}");
            json.Append(",\"highSchool\":{\"capacity\":").Append(d.GetEducation2Capacity())
                .Append(",\"need\":").Append(d.GetEducation2Need())
                .Append(",\"ratePercent\":").Append(d.GetEducation2Rate()).Append("}");
            json.Append(",\"university\":{\"capacity\":").Append(d.GetEducation3Capacity())
                .Append(",\"need\":").Append(d.GetEducation3Need())
                .Append(",\"ratePercent\":").Append(d.GetEducation3Rate()).Append("}");
            json.Append(",\"libraryCapacity\":").Append(d.GetLibraryCapacity());
            json.Append("}");

            json.Append(",\"crime\":{\"criminals\":").Append(d.GetCriminalAmount())
                .Append(",\"jailCapacity\":").Append(d.GetCriminalCapacity())
                .Append(",\"extraCriminals\":").Append(d.GetExtraCriminals()).Append("}");

            json.Append(",\"jobs\":{\"workers\":").Append(d.GetWorkerCount())
                .Append(",\"workplaces\":").Append(d.GetWorkplaceCount())
                .Append(",\"unemploymentPercent\":").Append(d.GetUnemployment()).Append("}");

            json.Append(",\"landValue\":").Append(d.GetLandValue());
            json.Append(",\"groundPollution\":").Append(d.GetGroundPollution());
            json.Append(",\"units\":\"raw game units from District.Get*; electricity in kW (MW = /1000)\"");
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        private static void AppendPair(StringBuilder json, string name, string aName, int a, string bName, int b)
        {
            json.Append(",\"").Append(name).Append("\":{\"").Append(aName).Append("\":").Append(a)
                .Append(",\"").Append(bName).Append("\":").Append(b)
                .Append(",\"surplus\":").Append(a - b).Append("}");
        }
    }
}

using System.Collections.Generic;

namespace VmsMap.Domain
{
    public interface IObjectProps: IIdentifiable
    {
        public List<ObjExtraPropertyDTO>? extra_props { get; set; }
    }
}
